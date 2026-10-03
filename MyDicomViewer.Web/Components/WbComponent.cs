using Microsoft.AspNetCore.Components;
using MyDicomViewer.Web.State;

namespace MyDicomViewer.Web.Components;

/// <summary>状態が変わったら描き直す部品の元</summary>
public abstract class WbComponent : ComponentBase, IDisposable
{
    [Inject] protected Workbench W { get; set; } = default!;

    protected override void OnInitialized() => W.Changed += OnWorkbenchChanged;

    protected virtual void OnWorkbenchChanged() => InvokeAsync(StateHasChanged);

    protected static string F(double v, string format = "0.#") => v.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    public virtual void Dispose()
    {
        W.Changed -= OnWorkbenchChanged;
        GC.SuppressFinalize(this);
    }
}
