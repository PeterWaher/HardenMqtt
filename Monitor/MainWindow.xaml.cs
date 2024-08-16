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
using Waher.Events.Files;
using System.IO;
using Waher.Networking.MQTT;

namespace Monitor
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private readonly TaskCompletionSource<MqttViewModel> initialized = new TaskCompletionSource<MqttViewModel>();
		private FilesProvider dbProvider = null;
		private bool loaded = false;

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
				// Exception types that are logged with an elevated type.
				Log.RegisterAlertExceptionType(true,
					typeof(OutOfMemoryException),
					typeof(StackOverflowException),
					typeof(AccessViolationException),
					typeof(InsufficientMemoryException));

				// Setup database
				Log.Register(new XmlFileEventSink("Events.xml", Path.Combine(Environment.CurrentDirectory, "Events", "Events.xml"), 7));
				Log.Informational("Setting up database...");

				dbProvider = await FilesProvider.CreateAsync("Database", "Default", 8192, 10000, 8192, Encoding.UTF8, 10000, true, false);
				Database.Register(dbProvider);

				// Repair database, if an inproper shutdown is detected
				await dbProvider.RepairIfInproperShutdown(string.Empty);

				// Starting internal modules
				await Types.StartAllModules(60000);

				this.initialized.TrySetResult(new MqttViewModel());
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
				this.initialized.TrySetException(ex);
			}
		}

		private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
		{
			Log.Informational("Monitor application stopping...");

			(this.DataContext as IDisposable)?.Dispose();

			this.dbProvider?.Flush().Wait();

			Types.StopAllModules().Wait();
			Log.Terminate();
		}

		private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
		{
			((MqttViewModel)this.DataContext).Password = ((PasswordBox)sender).Password;
		}

		private async void Window_Activated(object sender, EventArgs e)
		{
			if (!this.loaded)
			{
				this.loaded = true;

				try
				{
					await ((MqttViewModel)this.DataContext).Load();
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
					MessageBox.Show(ex.Message);
				}
			}
		}

		private void TopicTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
		{
			((MqttViewModel)this.DataContext).SelectedTopic = (((TreeView)sender).SelectedItem as MqttTopic)?.Topic;
		}

		private void ListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
		{
			if (sender is ListView ListView && ListView.SelectedItem is MqttContent Content && Content.Data.Length <= 4096)
			{
				string s = Encoding.UTF8.GetString(Content.Data);
				((MqttViewModel)this.DataContext).SelectedContent = s;
			}
		}
	}
}
