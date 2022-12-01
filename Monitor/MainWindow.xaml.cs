using System.Text;
using System.Windows;
using System.Windows.Controls;
using Waher.Events;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory.Loader;
using Waher.Runtime.Inventory;
using System.Threading.Tasks;
using System;
using Monitor.Model;

namespace Monitor
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private FilesProvider dbProvider = null;
		private TaskCompletionSource<MqttViewModel> initialized = new TaskCompletionSource<MqttViewModel>();

		public MainWindow()
		{
			InitializeComponent();

			// First, initialize environment and type inventory. This creates an inventory of types used by the application.
			// This is important for tasks such as data persistence, for example.
			TypesLoader.Initialize();

			// Setup Event Output to Console window
			Log.Informational("Monitor application starting...");

			Task.Run(() => this.Init());

			this.DataContext = this.initialized.Task.Result;
			if (this.DataContext is null)
				throw new Exception("Unable to initialize application.");
		}

		private async void Init()
		{
			try
			{
				// Setup database
				Log.Informational("Setting up database...");
				dbProvider = await FilesProvider.CreateAsync("Database", "Default", 8192, 10000, 8192, Encoding.UTF8, 10000, true, false);
				Database.Register(dbProvider);

				// Repair database, if an inproper shutdown is detected
				await dbProvider.RepairIfInproperShutdown(string.Empty);

				// Starting internal modules
				await Types.StartAllModules(60000);

				this.initialized.TrySetResult(await MqttViewModel.Create());
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
				this.initialized.TrySetResult(null);
			}
		}

		private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			Log.Informational("Monitor application stopping...");

			(this.DataContext as IDisposable)?.Dispose();

			if (!(this.dbProvider is null))
				this.dbProvider.Flush().Wait();

			Types.StopAllModules().Wait();
			Log.Terminate();
		}

		private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
		{
			((MqttViewModel)this.DataContext).Password = ((PasswordBox)sender).Password;
		}
	}
}
