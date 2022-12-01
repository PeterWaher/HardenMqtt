using System.Windows;
using System.Windows.Controls;

namespace Monitor
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		public MainWindow()
		{
			InitializeComponent();
			this.DataContext = new MqttViewModel();
		}

		private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
		{
			((MqttViewModel)this.DataContext).Password = ((PasswordBox)sender).Password;
		}
	}
}
