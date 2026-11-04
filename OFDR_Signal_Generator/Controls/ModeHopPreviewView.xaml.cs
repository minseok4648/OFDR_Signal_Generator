using System.Windows.Controls;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator.Controls
{
    public partial class ModeHopPreviewView : UserControl
    {
        private TimeRangeRequestedEventArgs pendingRange;
        private bool navigating;
        internal bool IsNavigating => navigating;
        public ModeHopPreviewView() { InitializeComponent(); }
        private async void Overlay_RangeRequested(object sender, TimeRangeRequestedEventArgs e)
        {
            pendingRange = e;
            if (navigating) return;
            navigating = true;
            try
            {
                while (pendingRange != null)
                {
                    var range = pendingRange; pendingRange = null;
                    var vm = DataContext as ModeHopPreviewViewModel;
                    if (vm == null || vm.IsBusy) continue;
                    await vm.ShowRangeAsync(range.StartSeconds, range.EndSeconds, true);
                }
            }
            finally { navigating = false; }
        }
    }
}
