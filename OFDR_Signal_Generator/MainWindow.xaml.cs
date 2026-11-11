using System.Windows;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            var smoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--mode-hop-smoke") >= 0;
            var fourChannelSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--four-channel-smoke") >= 0;
            var temperatureSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--notebook-temperature-smoke") >= 0;
            var rawWorkflowSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--notebook-raw-workflow-smoke") >= 0;
            var legacyComparisonSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--legacy-no-mode-hop-smoke") >= 0;
            var fftViewportSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--fft-viewport-smoke") >= 0;
            var reflectogramSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--reflectogram-smoke") >= 0;
            var distributedSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--distributed-smoke") >= 0;
            var indexedCsvSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--indexed-csv-smoke") >= 0;
            var unifiedSmoke = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--unified-smoke") >= 0;
            if (smoke || fourChannelSmoke || temperatureSmoke || rawWorkflowSmoke || legacyComparisonSmoke || fftViewportSmoke || reflectogramSmoke || distributedSmoke || indexedCsvSmoke || unifiedSmoke)
            {
                Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Validation.ModeHopGuiSmoke.AttachBindingTrace();
            }
            InitializeComponent();
            var viewModel = new OfdrWorkspaceViewModel();
            DataContext = viewModel;
            Closed += delegate { viewModel.Dispose(); };
            var waitingToClose = false;
            Closing += async (sender, e) =>
            {
                if (!viewModel.ModeHopPreview.IsBusy && !viewModel.IsBusy && !viewModel.FinalComparison.IsBusy && !viewModel.Reflectogram.IsBusy && !viewModel.DistributedComparison.IsBusy && !viewModel.Unified.IsBusy) return;
                e.Cancel = true;
                if (waitingToClose) return;
                waitingToClose = true;
                await viewModel.Unified.CancelAndWaitAsync();
                await viewModel.ModeHopPreview.CancelAndWaitAsync();
                await viewModel.CancelAndWaitAsync();
                await viewModel.FinalComparison.CancelAndWaitAsync();
                await viewModel.Reflectogram.CancelAndWaitAsync();
                await viewModel.DistributedComparison.CancelAndWaitAsync();
                Close();
            };
            if (smoke) Loaded += async delegate { await Validation.ModeHopGuiSmoke.RunAsync(this, viewModel); };
            if (fourChannelSmoke) Loaded += async delegate { await Validation.FourChannelGuiSmoke.RunAsync(this, viewModel); };
            if (temperatureSmoke) Loaded += async delegate { await Validation.NotebookTemperatureGuiSmoke.RunAsync(this, viewModel); };
            if (rawWorkflowSmoke) Loaded += async delegate { await Validation.NotebookRawWorkflowGuiSmoke.RunAsync(this, viewModel); };
            if (legacyComparisonSmoke) Loaded += async delegate { await Validation.LegacyModeHopComparisonSmoke.RunAsync(this, viewModel); };
            if (fftViewportSmoke) Loaded += async delegate { await Validation.FftViewportGuiSmoke.RunAsync(this, viewModel); };
            if (reflectogramSmoke) Loaded += async delegate { await Validation.ReflectogramGuiSmoke.RunAsync(this, viewModel); };
            if (distributedSmoke) Loaded += async delegate { await Validation.DistributedComparisonGuiSmoke.RunAsync(this, viewModel); };
            if (indexedCsvSmoke) Loaded += async delegate { await Validation.IndexedCsvGuiSmoke.RunAsync(this, viewModel); };
            if (unifiedSmoke) Loaded += async delegate { await Validation.UnifiedAnalysisGuiSmoke.RunAsync(this, viewModel); };
            var resultArgument = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "--distributed-result");
            if (!distributedSmoke && resultArgument >= 0 && resultArgument + 1 < System.Environment.GetCommandLineArgs().Length)
                Loaded += delegate {
                    try { viewModel.DistributedComparison.Load(System.Environment.GetCommandLineArgs()[resultArgument + 1]); WorkspaceTabs.SelectedIndex = 3; }
                    catch (System.Exception ex) { MessageBox.Show(this, ex.Message, "분포 결과 불러오기 실패", MessageBoxButton.OK, MessageBoxImage.Error); }
                };
        }
    }
}
