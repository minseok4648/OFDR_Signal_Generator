using System.Windows.Controls;
using OFDR_Signal_Generator.ViewModels;
namespace OFDR_Signal_Generator.Controls
{
    public partial class AuxiliaryQualityView : UserControl
    {
        public AuxiliaryQualityView() { InitializeComponent(); }
        private void Signal_RangeRequested(object sender, TimeRangeRequestedEventArgs e) => (DataContext as AuxiliaryQualityViewModel)?.SelectRange(e.StartSeconds, e.EndSeconds);
    }
}
