namespace LARGA.ManagerWeb.Services.Tours;

/// <summary>
/// One stop on a page tour. <see cref="Target"/> is the name used in the page's
/// <c>data-tour="..."</c> attribute (never a CSS class). <see cref="ManagerOnly"/> marks an
/// action only the Manager can do: the Manager sees it as a normal step, an Assistant Manager
/// sees a short "Only the Manager can do this" note instead.
/// </summary>
public sealed record TourStep(string Target, string Title, string Body, bool ManagerOnly = false);

/// <summary>
/// A page's tour. Bump <see cref="Version"/> after changing a tour in a way people should see
/// again: it makes the tour start by itself once more for everyone.
/// <see cref="FaqCategory"/> is the id of the Help &amp; FAQs category the last step links to.
/// </summary>
public sealed record PageTour(string Id, int Version, string FaqCategory, IReadOnlyList<TourStep> Steps);

/// <summary>A step ready to hand to the browser. A null <see cref="Target"/> is a centred note.</summary>
public sealed record ResolvedTourStep(string? Target, string Title, string Body, string? FaqHref);
