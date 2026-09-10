using LARGA.MobileApp.ViewModels.Manager;

namespace LARGA.MobileApp.Views.Manager;

public class AlertTemplateSelector : DataTemplateSelector
{
    public DataTemplate? SosTemplate { get; set; }
    public DataTemplate? FuelDiscrepancyTemplate { get; set; }
    public DataTemplate? ShiftApprovalTemplate { get; set; }

    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        var alert = (AlertItem)item;
        return alert.Type switch
        {
            AlertType.Sos => SosTemplate!,
            AlertType.FuelDiscrepancy => FuelDiscrepancyTemplate!,
            AlertType.ShiftApproval => ShiftApprovalTemplate!,
            _ => SosTemplate!
        };
    }
}