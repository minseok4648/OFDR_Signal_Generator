using System.Windows;
using OFDR_Signal_Generator.ViewModels;

namespace OFDR_Signal_Generator
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            var viewModel = new OfdrWorkspaceViewModel();
            DataContext = viewModel;
            Closed += delegate { viewModel.Dispose(); };
        }
    }
}
