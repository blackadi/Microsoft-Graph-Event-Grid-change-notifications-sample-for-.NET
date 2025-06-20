// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using GraphEventGrid.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Text.Json;

namespace GraphEventGrid.Handlers;

/// <summary>
/// Implements handlers for incoming notifications
/// from Azure Event Grid.
/// </summary>
public static class NotificationEventHandler
{
    private static readonly string HandlerEndpoint = "/notifications";

    /// <summary>
    /// Maps the notification event endpoints.
    /// </summary>
    /// <param name="app">The <see cref="WebApplication"/> instance to map endpoints with.</param>
    public static void Map(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        app.MapMethods(HandlerEndpoint, new[] { "OPTIONS" }, ValidateEndpoint);
        app.MapPost(HandlerEndpoint, HandleNotificationAsync);
        app.MapGet($"{HandlerEndpoint}/delete/{{id}}", HandleDeleteSubscriptionAsync);
        app.MapGet($"{HandlerEndpoint}/create/{{id}}", HandleCreateSubscriptionAsync); // <-- Add this line
    }

    private static void ValidateEndpoint(
        HttpContext context,
        [FromHeader(Name = "WEBHOOK-REQUEST-ORIGIN")] string? origin,
        [FromHeader(Name = "WEBHOOK-REQUEST-RATE")] string? rate)
    {
        // See https://github.com/cloudevents/spec/blob/v1.0/http-webhook.md#4-abuse-protection
        // Event Grid sends the host that emits events in this header as a request
        // for our webhook to allow them to send
        if (!string.IsNullOrEmpty(origin))
        {
            context.Response.Headers.Append("WebHook-Allowed-Origin", origin);
        }

        if (!string.IsNullOrEmpty(rate))
        {
            context.Response.Headers.Append("WebHook-Allowed-Rate", rate);
        }
    }

    private static async Task<IResult> HandleNotificationAsync(
        CloudEventNotification notification,
        [FromServices] GraphServiceClient graphClient,
        [FromServices] ILogger<Program> logger)
    {
        if (!string.IsNullOrEmpty(notification.Type))
        {
            logger.LogInformation("Received {type} notification from Event Grid", notification.Type);

            try
            {
                if (notification.Type.Equals("Microsoft.Graph.UserUpdated", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleUserUpdateAsync(
                        notification.GetChangeNotification(), graphClient, logger);
                }
                else if (notification.Type.Equals("Microsoft.Graph.GroupUpdated", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleGroupUpdateAsync(
                        notification.GetChangeNotification(), graphClient, logger);
                }
                else if (notification.Type.Equals("Microsoft.Graph.UserDeleted", StringComparison.OrdinalIgnoreCase))
                {
                    HandleUserDelete(notification.GetChangeNotification(), logger);
                }
                else if (notification.Type.Equals("Microsoft.Graph.SubscriptionReauthorizationRequired", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleSubscriptionRenewalAsync(
                        notification.GetChangeNotification(), graphClient, logger);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing notification");
            }
        }
        else
        {
            logger.LogWarning("Received notification with no type: " + notification.Type + "\nnotification.Source" + notification.Source + "\nnotification.Data" + notification.Data);
        }

        return Results.Accepted();
    }

    private static async Task HandleGroupUpdateAsync(
        Microsoft.Graph.Models.ChangeNotification? notification,
        GraphServiceClient graphClient,
        ILogger logger)
    {
        if (notification is not null)
        {
            // The group was either created, updated, or soft-deleted.
            // The notification only contains the group's ID, so
            // get the group from Microsoft Graph if other details are needed.
            // If the group isn't found, then it was likely deleted.

            // The notification has the relative URL to the group. The .WithUrl method
            // in the Graph client can use a URL to retrieve an object.
            try
            {
                var group = await graphClient.Groups[string.Empty]
                    .WithUrl($"{graphClient.RequestAdapter.BaseUrl}/{notification.Resource}")
                    .GetAsync();

                logger.LogInformation(
                    "Group {name} (ID: {id}) was created or updated",
                    group?.DisplayName,
                    group?.Id);

                // fetch the first page of groups
                var deltaResponse = await graphClient.Groups.Delta.GetAsDeltaGetResponseAsync((requestConfiguration) =>
                {
                    requestConfiguration.QueryParameters.Filter = $"id eq '{group?.Id}'"; // filter by the group ID
                    requestConfiguration.QueryParameters.Select = new string[] { "id", "displayName", "description", "members" };
                    requestConfiguration.QueryParameters.Expand = new string[] { "members($select=id,userPrincipalName,displayName)" };
                    requestConfiguration.Headers.Add("Prefer", "return=minimal");
                });

                // create a list to hold the groups
                List<Group> groupsDeltaPage = [];

                List<string> removedMembers = [];
                List<string> addedMembers = [];

                // create a page iterator to iterate through the pages of the response
                var pageIterator = PageIterator<Microsoft.Graph.Models.Group, Microsoft.Graph.Groups.Delta.DeltaGetResponse>.CreatePageIterator(graphClient, deltaResponse, group =>
                {
                    groupsDeltaPage.Add(group);
                    return true;
                });

                // This will iterate follow through the odata.nextLink until the last page is reached with an odata.deltaLink
                await pageIterator.IterateAsync();

                if (pageIterator.State == PagingState.Delta)
                {
                    await Task.Delay(5000); // wait for some time for changes to occur.

                    // call delta again with the deltaLink to get the next page of results
                    // Console.WriteLine("Calling delta again with deltaLink");
                    // Console.WriteLine("DeltaLink url is: " + pageIterator.Deltalink);

                    deltaResponse = await graphClient.Groups.Delta.WithUrl(pageIterator.Deltalink).GetAsDeltaGetResponseAsync();

                    logger.LogInformation(
                    "before delta removedMembers size {count} and addedMembers size {count}",
                    removedMembers.Count,
                    addedMembers.Count);

                    groupsDeltaPage.ForEach(async group =>
                    {
                        if (group.AdditionalData.TryGetValue("members@delta", out var membersDelta)) // get the info in the additional data bag.
                        {
                            var memberListJsonString = await KiotaJsonSerializer.SerializeAsStringAsync((UntypedArray)membersDelta);
                            var membersObjectList = await KiotaJsonSerializer.DeserializeCollectionAsync<Microsoft.Graph.Models.DirectoryObject>(memberListJsonString);
                            group.Members = [.. membersObjectList];

                            // Console.WriteLine($"Group members delta: {group.Members.Count} members found in the group.");
                            group.Members.ForEach(async member =>
                            {
                                if (member is Microsoft.Graph.Models.User user)
                                {
                                    await GetDeltaReason(user, removedMembers, addedMembers);
                                }
                                else if (member is Microsoft.Graph.Models.Group group)
                                {
                                    await GetDeltaReason(group, removedMembers, addedMembers);
                                }
                                else if (member is Microsoft.Graph.Models.ServicePrincipal servicePrincipal)
                                {
                                    await GetDeltaReason(servicePrincipal, removedMembers, addedMembers);
                                }
                                else
                                {
                                    Console.WriteLine("Unknown member type");
                                }
                            });
                        }
                    });

                    await pageIterator.ResumeAsync();
                }

                logger.LogInformation(
                    "After delta removedMembers size {count} and addedMembers size {count}",
                    removedMembers.Count,
                    addedMembers.Count);

                // Convert removedMembers to List<User> and addedMembers to List<User>
                List<User> removedUsers = [];
                List<User> addedUsers = [];
                foreach (var member in removedMembers) {
                    var user = await FetchUserAsync(graphClient, member);
                    if (user != null)
                    {
                        removedUsers.Add(user);
                    }
                }

                foreach (var member in addedMembers) {
                    var user = await FetchUserAsync(graphClient, member);
                    if (user != null)
                    {
                        addedUsers.Add(user);
                    }
                }

                PrintUsers("Added User -> ", addedUsers);
                PrintUsers("Removed User -> ", removedUsers);

            }
            catch (ODataError oDataError)
            {
                if (oDataError.Error?.Code is string errorCode &&
                    errorCode.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    var groupId = notification.Resource?.Split("/")[1];
                    logger.LogInformation("Group with ID {groupId} was soft-deleted", groupId);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private static async Task GetDeltaReason(Microsoft.Graph.Models.DirectoryObject directoryObject, List<string> removedMembers, List<string> addedMembers)
    {
        switch (directoryObject)
        {
            case Microsoft.Graph.Models.User user:
                // Console.WriteLine("Only User ID will be returned with the delta link under the members@delta prperty, if more user info is needed make another GET request to the /users/{id}");
                if (user.AdditionalData.TryGetValue("@removed", out var removedUser))
                {
                    // Console.WriteLine(removedUser.ToString());
                    var reasonString = await KiotaJsonSerializer.SerializeAsStringAsync((UntypedObject)removedUser);

                    // Only for removed users member the reason will be returned.
                    Console.WriteLine($"User ID: {user.Id}, reason: {reasonString}");

                    removedMembers.Add(user.Id);
                }
                else
                {
                    addedMembers.Add(user.Id);
                }

                break;

            case Microsoft.Graph.Models.Group group:
                // Console.WriteLine("Only Group ID will be returned with the delta link under the members@delta prperty, if more Group info is needed make another GET request to the /Group/{id}");
                if (group.AdditionalData.TryGetValue("@removed", out var removedGroup))
                {
                    // Console.WriteLine(removedGroup.ToString());
                    var reasonString = await KiotaJsonSerializer.SerializeAsStringAsync((UntypedObject)removedGroup);

                    // Only for removed Group member the reason will be returned.
                    Console.WriteLine($"Group ID: {group.Id}, reason: {reasonString}");

                    removedMembers.Add(group.Id);
                }
                else
                {
                    addedMembers.Add(group.Id);
                }

                break;

            case Microsoft.Graph.Models.ServicePrincipal servicePrincipal:
                // Console.WriteLine("Only ServicePrincipal ID will be returned with the delta link under the members@delta prperty, if more ServicePrincipal info is needed make another GET request to the /ServicePrincipal/{id}");
                if (servicePrincipal.AdditionalData.TryGetValue("@removed", out var removedServicePrincipal))
                {
                    // Console.WriteLine(removedServicePrincipal.ToString());
                    var reasonString = await KiotaJsonSerializer.SerializeAsStringAsync((UntypedObject)removedServicePrincipal);

                    // Only for removed Group member the reason will be returned.
                    Console.WriteLine($"Service Principal ID: {servicePrincipal.Id}, reason: {reasonString}");

                    removedMembers.Add(servicePrincipal.Id);
                }
                else
                {
                    addedMembers.Add(servicePrincipal.Id);
                }

                break;

            default:
                Console.WriteLine("Unknown directory object type");
                break;
        }
    }

    private static async Task HandleUserUpdateAsync(
        Microsoft.Graph.Models.ChangeNotification? notification,
        GraphServiceClient graphClient,
        ILogger logger)
    {
        if (notification is not null)
        {
            // The user was either created, updated, or soft-deleted.
            // The notification only contains the user's ID, so
            // get the user from Microsoft Graph if other details are needed.
            // If the user isn't found, then it was likely deleted.

            // The notification has the relative URL to the user. The .WithUrl method
            // in the Graph client can use a URL to retrieve an object.
            try
            {
                var user = await graphClient.Users[string.Empty]
                    .WithUrl($"{graphClient.RequestAdapter.BaseUrl}/{notification.Resource}")
                    .GetAsync();

                logger.LogInformation(
                    "User {name} (ID: {id}) was created or updated",
                    user?.DisplayName,
                    user?.Id);
            }
            catch (ODataError oDataError)
            {
                if (oDataError.Error?.Code is string errorCode &&
                    errorCode.Contains("ResourceNotFound", StringComparison.OrdinalIgnoreCase))
                {
                    var userId = notification.Resource?.Split("/")[1];
                    logger.LogInformation("User with ID {userId} was soft-deleted", userId);
                }
                else
                {
                    throw;
                }
            }
        }
    }

    private static void HandleUserDelete(
        Microsoft.Graph.Models.ChangeNotification? notification,
        ILogger logger)
    {
        if (notification is not null)
        {
            // The user was permanently deleted. The notification only contains
            // the user's ID, and we can no longer get the user from Graph.
            var userId = notification.Resource?.Split("/")[1];
            logger.LogInformation("User with ID {userId} was deleted", userId);
        }
    }

    private static async Task HandleSubscriptionRenewalAsync(
        Microsoft.Graph.Models.ChangeNotification? notification,
        GraphServiceClient graphClient,
        ILogger logger)
    {
        if (notification is not null)
        {
            // The subscription needs to be renewed.
            if (notification.SubscriptionId?.ToString() is string subscriptionId)
            {
                await graphClient.Subscriptions[subscriptionId]
                    .PatchAsync(new()
                    {
                        ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
                    });

                logger.LogInformation("Subscription with ID {id} renewed for another hour", subscriptionId);
            }
        }
    }

    private static async Task<User?> FetchUserAsync(GraphServiceClient graphClient, string memberId)
    {
        try
        {
            var usersResponse = await graphClient.Users[memberId].GetAsync(requestConfiguration => requestConfiguration.QueryParameters.Select = new string[] { "id", "createdDateTime", "displayName", "userPrincipalName" });
            return usersResponse;
        }
        catch (ODataError)
        {
            return null;
        }
    }

    private static void PrintUsers(string label, List<User> users)
    {
        users.ForEach(user =>
        {
            Console.WriteLine($"{label} User ID: {user.Id}, Display Name: {user.DisplayName}, UserPrincipalName: {user.UserPrincipalName}, Created Date: {user.CreatedDateTime}");
        });
    }
    /// <summary>
    /// Handles GET requests to /notifications/{id} to delete a subscription from Graph API.
    /// </summary>
    /// <param name="id">The subscription ID to delete.</param>
    /// <param name="graphClient">The GraphServiceClient instance.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>An IResult indicating the outcome.</returns>
    private static async Task<IResult> HandleDeleteSubscriptionAsync(
        [FromRoute] string id,
        [FromServices] GraphServiceClient graphClient,
        [FromServices] ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            logger.LogWarning("No subscription ID provided for deletion.");
            return Results.BadRequest("Subscription ID is required.");
        }

        try
        {
            await graphClient.Subscriptions[id].DeleteAsync();
            logger.LogInformation("Subscription with ID {id} deleted successfully.", id);
            return Results.Ok($"Subscription {id} deleted.");
        }
        catch (ODataError oDataError)
        {
            logger.LogError(oDataError, "Failed to delete subscription with ID {id}.", id);
            return Results.NotFound($"Subscription {id} not found or could not be deleted.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error deleting subscription with ID {id}.", id);
            return Results.Problem("An unexpected error occurred.");
        }
    }
    /// <summary>
    /// Handles GET requests to /notifications/{id} to create a new subscription in Graph API.
    /// </summary>
    /// <param name="id">The resource ID to subscribe to.</param>
    /// <param name="settings">The application settings.</param>
    /// <param name="graphClient">The GraphServiceClient instance.</param>
    /// <param name="logger">The logger instance.</param>
    /// <returns>An IResult indicating the outcome.</returns>
    private static async Task<IResult> HandleCreateSubscriptionAsync(
        [FromRoute] string id,
        AppSettings settings,
        [FromServices] GraphServiceClient graphClient,
        [FromServices] ILogger<Program> logger)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            logger.LogWarning("No resource ID provided for subscription creation.");
            return Results.BadRequest("Resource ID is required.");
        }

        var subscriptions = await graphClient.Subscriptions.GetAsync();
        if (subscriptions?.Value?.Count > 0)
        {
            logger.LogInformation("Subscription already exists with ID {}", subscriptions.Value[0].Id);

            // await graphClient.Subscriptions[subscriptions.Value[0].Id].DeleteAsync();

            // var test = await graphClient.Subscriptions.GetAsync();
            // logger.LogInformation("Deleted existing subscription, remaining subscriptions: {count}", test?.Value?.Count ?? 0);
            return Results.BadRequest("Subscription already exists with ID " + subscriptions.Value[0].Id);
        }

        try
        {
            // Create a subscription
            logger.LogInformation("No existing subscription found");

            var eventGridUrl =
                $"EventGrid:?azuresubscriptionid={settings.SubscriptionId}" +
                $"&resourcegroup={settings.ResourceGroup}" +
                $"&partnertopic={settings.EventGridTopic}" +
                $"&location={settings.Location}";

            var newSubscription = await graphClient.Subscriptions.PostAsync(new Subscription
            {
                ChangeType = "updated,deleted",
                Resource = $"groups/{id}/members",
                ClientState = "test@123",
                NotificationUrl = eventGridUrl,
                LifecycleNotificationUrl = eventGridUrl,

                // Setting `a short expire time for testing purposes
                ExpirationDateTime = DateTimeOffset.UtcNow.AddHours(1),
            });

            if (newSubscription is null)
            {
                logger.LogError("Could not create subscription - the API returned null");
                return Results.BadRequest("Failed to create subscription. The API returned null.");
            }
            else
            {
                logger.LogInformation(
                    "Created new subscription with ID {subscriptionId }", newSubscription.Id + "\nExpirationDateTime: " + newSubscription.ExpirationDateTime + "\n NotificationUrl: " + newSubscription.NotificationUrl + "\n NotificationUrlAppId: " + newSubscription.NotificationUrlAppId + "\n Resource: " + newSubscription.Resource);
                logger.LogInformation(
                    "Please activate the {topicName} partner topic in the Azure portal and create an event subscription. See README for details.",
                    settings.EventGridTopic);

                return Results.Ok(new
                {
                    Message = $"Subscription created for resource {newSubscription.Resource}.",
                    SubscriptionId = newSubscription.Id,
                });
            }
        }
        catch (ODataError oDataError)
        {
            logger.LogError(oDataError, "Failed to create subscription for resource {id}.", id);
            return Results.Problem("Failed to create subscription.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating subscription for resource {id}.", id);
            return Results.Problem("An unexpected error occurred.");
        }
    }

}
