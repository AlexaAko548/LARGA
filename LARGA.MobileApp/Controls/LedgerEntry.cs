namespace LARGA.MobileApp.Controls;

/// <summary>
/// Entry used in the Quick Ledger forms. The platform underline is removed in MauiProgram, so the text sits
/// inside its light-blue field the way the other ledger fields do. Only this type is affected, so other
/// screens keep their normal entries.
/// </summary>
public class LedgerEntry : Microsoft.Maui.Controls.Entry
{
}
