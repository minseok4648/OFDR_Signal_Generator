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
            if (smoke || fourChannelSmoke || temperatureSmoke || rawWorkflowSmoke)
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
                if (!viewModel.ModeHopPreview.IsBusy && !viewModel.IsBusy && !viewModel.FinalComparison.IsBusy) return;
                e.Cancel = true;
                if (waitingToClose) return;
                waitingToClose = true;
                await viewModel.ModeHopPreview.CancelAndWaitAsync();
                await viewModel.CancelAndWaitAsync();
                await viewModel.FinalComparison.CancelAndWaitAsync();
                Close();
            };
            if (smoke) Loaded += async delegate { await Validation.ModeHopGuiSmoke.RunAsync(this, viewModel); };
            if (fourChannelSmoke) Loaded += async delegate { await Validation.FourChannelGuiSmoke.RunAsync(this, viewModel); };
            if (temperatureSmoke) Loaded += async delegate { await Validation.NotebookTemperatureGuiSmoke.RunAsync(this, viewModel); };
            if (rawWorkflowSmoke) Loaded += async delegate { await Validation.NotebookRawWorkflowGuiSmoke.RunAsync(this, viewModel); };
        }
    }
}
