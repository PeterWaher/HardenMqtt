using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Waher.Events;
using Waher.Networking.MQTT;

namespace Monitor
{
	/// <summary>
	/// View model for the main window in the monitor app
	/// </summary>
	public class MqttViewModel : INotifyPropertyChanged
	{
		private MqttClient mqtt;
		private string host = string.Empty;
		private int port = 8883;
		private bool tls = true;
		private bool trustCertificate = false;
		private string userName = string.Empty;
		private string password = string.Empty;

		/// <summary>
		/// View model for the main window in the monitor app
		/// </summary>
		public MqttViewModel()
		{
			this.Connect = new Command(this.ExecuteConnect);
			this.Close = new Command(this.ExecuteClose);
			this.Clear = new Command(this.ExecuteClear);
		}

		/// <summary>
		/// Host Name of MQTT Broker
		/// </summary>
		public string Host
		{
			get => this.host;
			set
			{
				this.host = value;
				this.OnPropertyChanged(nameof(this.Host));
			}
		}

		/// <summary>
		/// Port Number of MQTT Broker
		/// </summary>
		public int Port
		{
			get => this.port;
			set
			{
				this.port = value;
				this.OnPropertyChanged(nameof(this.Port));
			}
		}

		/// <summary>
		/// If Transport Encryption using TLS is to be used.
		/// </summary>
		public bool Tls
		{
			get => this.tls;
			set
			{
				this.tls = value;
				this.OnPropertyChanged(nameof(this.Tls));
			}
		}

		/// <summary>
		/// If the server certificate should be trusted, even if it is not valid.
		/// </summary>
		public bool TrustCertificate
		{
			get => this.trustCertificate;
			set
			{
				this.trustCertificate = value;
				this.OnPropertyChanged(nameof(this.TrustCertificate));
			}
		}

		/// <summary>
		/// Optional User Name
		/// </summary>
		public string UserName
		{
			get => this.userName;
			set
			{
				this.userName = value;
				this.OnPropertyChanged(nameof(this.UserName));
			}
		}

		/// <summary>
		/// Optional Password
		/// </summary>
		public string Password
		{
			get => this.password;
			set
			{
				this.password = value;
				this.OnPropertyChanged(nameof(this.Password));
			}
		}

		/// <summary>
		/// Connection State
		/// </summary>
		public MqttState ConnectionState => this.mqtt?.State ?? MqttState.Offline;

		/// <summary>
		/// If the connection parameters can be edited.
		/// </summary>
		public bool CanEditConnection => this.ConnectionState == MqttState.Offline || this.ConnectionState == MqttState.Error;

		/// <summary>
		/// Connect command
		/// </summary>
		public Command Connect { get; }

		/// <summary>
		/// Close connection command
		/// </summary>
		public Command Close { get; }

		/// <summary>
		/// Clear command
		/// </summary>
		public Command Clear { get; }

		/// <summary>
		/// Event raised when a property in the view model has changed
		/// </summary>
		public event PropertyChangedEventHandler PropertyChanged;

		private void OnPropertyChanged(string PropertyName)
		{
			Application.Current.Dispatcher.Invoke(() =>
			{
				try
				{
					this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			});
		}

		/// <summary>
		/// Connects to the broker.
		/// </summary>
		private void ExecuteConnect()
		{
			try
			{
				this.mqtt?.Dispose();
				this.mqtt = null;

				this.mqtt = new MqttClient(this.Host, this.Port, this.Tls, this.UserName, this.Password)
				{
					TrustServer = this.TrustCertificate
				};

				this.mqtt.OnStateChanged += (_, __) =>
				{
					this.OnPropertyChanged(nameof(this.ConnectionState));
					this.OnPropertyChanged(nameof(this.CanEditConnection));
					this.Connect.Changed();
					return Task.CompletedTask;
				};

				this.Connect.Changed();
				this.Close.Changed();
			}
			catch (Exception ex)
			{
				this.mqtt?.Dispose();
				this.mqtt = null;

				Log.Critical(ex);
				MessageBox.Show(ex.Message);
			}
		}

		/// <summary>
		/// Closes the connection to the broker.
		/// </summary>
		private void ExecuteClose()
		{
			this.mqtt?.Dispose();
			this.mqtt = null;

			this.Connect.Changed();
		}

		/// <summary>
		/// Clears the window
		/// </summary>
		private void ExecuteClear()
		{
			// TODO
		}
	}
}
