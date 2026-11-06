using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace LARGA.ManagerWeb.Services.Tours;

/// <summary>
/// Starts page tours. Pages (and the (?) button) only call this class; it picks the tour for the
/// current page, filters it for the signed-in role, remembers who has seen what, and talks to
/// wwwroot/tour.js (driver.js) through IJSRuntime.
/// </summary>
public sealed class TourService : IAsyncDisposable
{
    /// <summary>Where the last step's link goes: /help#ledger.</summary>
    public const string HelpPath = "/help";

    /// <summary>What an Assistant Manager reads in place of a Manager-only step.</summary>
    public const string ManagerOnlyNote = "Only the Manager can do this.";

    private readonly IJSRuntime _js;
    private readonly AuthenticationStateProvider _auth;
    private readonly NavigationManager _navigation;
    private IJSObjectReference? _module;

    public TourService(IJSRuntime js, AuthenticationStateProvider auth, NavigationManager navigation)
    {
        _js = js;
        _auth = auth;
        _navigation = navigation;
    }

    /// <summary>The tour for the page being shown, or null when the page has none.</summary>
    public PageTour? Find(string? key = null) => TourCatalog.Find(key, _navigation.ToBaseRelativePath(_navigation.Uri));

    /// <summary>
    /// The steps a person gets: an Assistant Manager's Manager-only steps become a short note
    /// (nothing to point at, since the control is hidden from them), and the FAQ link is added last.
    /// </summary>
    public static IReadOnlyList<ResolvedTourStep> Resolve(PageTour tour, bool isManager)
    {
        var steps = new List<ResolvedTourStep>();
        foreach (TourStep step in tour.Steps)
        {
            if (step.ManagerOnly && !isManager)
            {
                steps.Add(new ResolvedTourStep(null, step.Title + " (Manager only)", ManagerOnlyNote, null));
            }
            else
            {
                steps.Add(new ResolvedTourStep(step.Target, step.Title, step.Body, null));
            }
        }

        steps.Add(new ResolvedTourStep(null, "Need more help?", "More questions? Open Help & FAQs.", $"{HelpPath}#{tour.FaqCategory}"));
        return steps;
    }

    /// <summary>The localStorage name for "this person has seen this tour": user, tour and version.</summary>
    public static string SeenKey(string userId, PageTour tour) => $"{userId}:{tour.Id}:v{tour.Version}";

    /// <summary>Starts the page's tour (the (?) button). Returns false when there is nothing to show.</summary>
    public Task<bool> StartAsync(string? key = null) => RunAsync(key, automatic: false);

    /// <summary>Starts the tour only if this person has never seen this version of it.</summary>
    public Task<bool> StartIfFirstVisitAsync(string? key = null) => RunAsync(key, automatic: true);

    private async Task<bool> RunAsync(string? key, bool automatic)
    {
        try
        {
            PageTour? tour = Find(key);
            if (tour is null)
            {
                return false;
            }

            ClaimsPrincipal user = (await _auth.GetAuthenticationStateAsync()).User;
            string userId = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty;
            bool isManager = user.IsInRole(ManagerSignInService.ManagerRole);
            if (automatic && string.IsNullOrEmpty(userId))
            {
                return false; // can't tell who has seen it
            }

            _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "/tour.js");
            string seenKey = SeenKey(userId, tour);
            if (automatic && await _module.InvokeAsync<bool>("hasSeen", seenKey))
            {
                return false;
            }

            bool started = await _module.InvokeAsync<bool>("startTour", Resolve(tour, isManager), new { automatic });
            if (started && !string.IsNullOrEmpty(userId))
            {
                await _module.InvokeVoidAsync("markSeen", seenKey);
            }

            return started;
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException or TaskCanceledException)
        {
            // A tour is a nicety: if the browser can't run it, the page carries on as normal.
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null)
        {
            return;
        }

        try
        {
            await _module.InvokeVoidAsync("stopTour");
            await _module.DisposeAsync();
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or InvalidOperationException)
        {
        }
    }
}
