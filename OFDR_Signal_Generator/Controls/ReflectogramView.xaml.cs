using System.Windows.Controls;
using System;
using System.ComponentModel;
using System.Windows.Threading;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator.Controls
{
    public partial class ReflectogramView : UserControl
    {
        public ReflectogramView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => {
                if (e.OldValue is ReflectogramViewModel oldVm) oldVm.PropertyChanged -= ResultChanged;
                if (e.NewValue is ReflectogramViewModel newVm) newVm.PropertyChanged += ResultChanged;
                SetDetailRanges();
            };
        }
        private void ResultChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ReflectogramViewModel.ReferencePlot) || e.PropertyName == nameof(ReflectogramViewModel.SignalPlot)) SetDetailRanges();
        }
        private void SetDetailRanges()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
                var vm = DataContext as ReflectogramViewModel;
                if (vm?.ReferenceResult != null) { var peak = vm.ReferenceResult.Metrics["C_peak_m"]; ReferenceDetail.SetFftRange(peak - .4, peak + .4); }
                if (vm?.SignalResult != null) { var peak = vm.SignalResult.Metrics["C_peak_m"]; SignalDetail.SetFftRange(peak - .4, peak + .4); }
            }));
        }
    }
}
