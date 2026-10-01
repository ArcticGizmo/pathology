using Avalonia.Controls;
using Pathology.App.Controls;
using Pathology.App.ViewModels;

namespace Pathology.App.Views;

public partial class EntriesView : UserControl
{
    public EntriesView()
    {
        InitializeComponent();
        KeepSelectionVisible.Attach(this, nameof(EntriesViewModel.Selected), vm => ((EntriesViewModel)vm).Selected);
    }
}
