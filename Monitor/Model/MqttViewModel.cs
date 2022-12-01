using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Waher.Events;
using Waher.Networking.MQTT;
using Waher.Runtime.Settings;

namespace Monitor.Model
{
	/// <summary>
	/// View model for the main window in the monitor app
	/// </summary>
	public class MqttViewModel : ViewModel, IDisposable
	{
		private readonly BindableProperty<string> host;
		private readonly BindableProperty<int> port;
		private readonly BindableProperty<bool> tls;
		private readonly BindableProperty<bool> trustCertificate;
		private readonly BindableProperty<string> userName;
		private readonly BindableProperty<string> password;
		private readonly BindableProperty<MqttState> state;
		private readonly BindableProperty<bool> connected;
		private readonly BindableProperty<string> lastTopic;
		private readonly BindableProperty<string> selectedTopic;
		private readonly BindableProperty<string> selectedContent;
		private readonly BindableProperty<MqttQualityOfService> qos;
		private readonly BindableProperty<bool> retain;
		private readonly Dictionary<string, MqttTopic> topics = new Dictionary<string, MqttTopic>();
		private readonly ObservableCollection<MqttTopic> rootTopics = new ObservableCollection<MqttTopic>();
		private readonly ObservableCollection<MqttContent> messages = new ObservableCollection<MqttContent>();
		private MqttClient mqtt;

		/// <summary>
		/// View model for the main window in the monitor app
		/// </summary>
		public MqttViewModel()
			: base()
		{
			this.host = new BindableProperty<string>(this, nameof(this.Host), string.Empty);
			this.port = new BindableProperty<int>(this, nameof(this.Port), 8883);
			this.tls = new BindableProperty<bool>(this, nameof(this.Tls), true);
			this.trustCertificate = new BindableProperty<bool>(this, nameof(this.TrustCertificate), false);
			this.userName = new BindableProperty<string>(this, nameof(this.UserName), string.Empty);
			this.password = new BindableProperty<string>(this, nameof(this.Password), string.Empty);

			this.state = new BindableProperty<MqttState>(this, nameof(this.State), MqttState.Offline);
			this.connected = new BindableProperty<bool>(this, nameof(this.Connected), false);
			this.lastTopic = new BindableProperty<string>(this, nameof(this.LastTopic), string.Empty);
			this.selectedTopic = new BindableProperty<string>(this, nameof(this.SelectedTopic), string.Empty);
			this.selectedContent = new BindableProperty<string>(this, nameof(this.SelectedContent), string.Empty);
			this.qos = new BindableProperty<MqttQualityOfService>(this, nameof(this.QoS), MqttQualityOfService.AtMostOnce);
			this.retain = new BindableProperty<bool>(this, nameof(this.Retain), false);

			this.Connect = new Command(this.ExecuteConnect);
			this.Close = new Command(this.ExecuteClose);
			this.Clear = new Command(this.ExecuteClear);
			this.Publish = new Command(this.ExecutePublish);
		}

		/// <summary>
		/// Loads the view model
		/// </summary>
		public async Task Load()
		{
			this.Host = await RuntimeSettings.GetAsync("MQTT.Host", this.Host);
			this.Port = (int)await RuntimeSettings.GetAsync("MQTT.Port", this.Port);
			this.Tls = await RuntimeSettings.GetAsync("MQTT.Tls", this.Tls);
			this.UserName = await RuntimeSettings.GetAsync("MQTT.UserName", this.UserName);
			this.Password = await RuntimeSettings.GetAsync("MQTT.Password", this.Password);
			this.TrustCertificate = await RuntimeSettings.GetAsync("MQTT.TrustServer", this.TrustCertificate);
		}

		/// <summary>
		/// Host Name of MQTT Broker
		/// </summary>
		public string Host
		{
			get => this.host.Value;
			set => this.host.Value = value;
		}

		/// <summary>
		/// Port Number of MQTT Broker
		/// </summary>
		public int Port
		{
			get => this.port.Value;
			set => this.port.Value = value;
		}

		/// <summary>
		/// If Transport Encryption using TLS is to be used.
		/// </summary>
		public bool Tls
		{
			get => this.tls.Value;
			set => this.tls.Value = value;
		}

		/// <summary>
		/// If the server certificate should be trusted, even if it is not valid.
		/// </summary>
		public bool TrustCertificate
		{
			get => this.trustCertificate.Value;
			set => this.trustCertificate.Value = value;
		}

		/// <summary>
		/// Optional User Name
		/// </summary>
		public string UserName
		{
			get => this.userName.Value;
			set => this.userName.Value = value;
		}

		/// <summary>
		/// Optional Password
		/// </summary>
		public string Password
		{
			get => this.password.Value;
			set => this.password.Value = value;
		}

		/// <summary>
		/// Current connection state
		/// </summary>
		public MqttState State
		{
			get => this.state.Value;
			set
			{
				this.state.Value = value;
				this.Connected = value == MqttState.Connected;
			}
		}

		/// <summary>
		/// If the connection to the broker is live.
		/// </summary>
		public bool Connected
		{
			get => this.connected.Value;
			set => this.connected.Value = value;
		}

		/// <summary>
		/// Last topic received.
		/// </summary>
		public string LastTopic
		{
			get => this.lastTopic.Value;
			set => this.lastTopic.Value = value;
		}

		/// <summary>
		/// Selected topic.
		/// </summary>
		public string SelectedTopic
		{
			get => this.selectedTopic.Value;
			set
			{
				this.selectedTopic.Value = value;
				this.messages.Clear();
			}
		}

		/// <summary>
		/// Selected content.
		/// </summary>
		public string SelectedContent
		{
			get => this.selectedContent.Value;
			set => this.selectedContent.Value = value;
		}

		/// <summary>
		/// Quality of service.
		/// </summary>
		public MqttQualityOfService QoS
		{
			get => this.qos.Value;
			set => this.qos.Value = value;
		}

		/// <summary>
		/// Retain content
		/// </summary>
		public bool Retain
		{
			get => this.retain.Value;
			set => this.retain.Value = value;
		}

		/// <summary>
		/// If the connection parameters can be edited.
		/// </summary>
		public bool CanEditConnection => this.State == MqttState.Offline || this.State == MqttState.Error;

		/// <summary>
		/// Root topics
		/// </summary>
		public ObservableCollection<MqttTopic> RootTopics => this.rootTopics;

		/// <summary>
		/// Messages
		/// </summary>
		public ObservableCollection<MqttContent> Messages => this.messages;

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
		/// Publish command
		/// </summary>
		public Command Publish { get; }

		/// <summary>
		/// Connects to the broker.
		/// </summary>
		private void ExecuteConnect()
		{
			try
			{
				this.mqtt?.Dispose();
				this.mqtt = null;
				this.State = MqttState.Offline;

				this.mqtt = new MqttClient(this.Host, this.Port, this.Tls, this.UserName, this.Password)
				{
					TrustServer = this.TrustCertificate
				};

				this.mqtt.OnStateChanged += this.Mqtt_OnStateChanged;
				this.mqtt.OnContentReceived += this.Mqtt_OnContentReceived;

				this.Connect.Changed();
				this.Close.Changed();
			}
			catch (Exception ex)
			{
				this.mqtt?.Dispose();
				this.mqtt = null;
				this.State = MqttState.Offline;

				Log.Critical(ex);
				MessageBox.Show(ex.Message);
			}
		}

		private Task Mqtt_OnStateChanged(object Sender, MqttState NewState)
		{
			this.State = NewState;

			this.OnPropertyChanged(nameof(this.CanEditConnection));
			this.Connect.Changed();

			if (NewState == MqttState.Connected)
			{
				Task.Run(async () =>
				{
					try
					{
						await RuntimeSettings.SetAsync("MQTT.Host", this.Host);
						await RuntimeSettings.SetAsync("MQTT.Port", this.Port);
						await RuntimeSettings.SetAsync("MQTT.Tls", this.Tls);
						await RuntimeSettings.SetAsync("MQTT.UserName", this.UserName);
						await RuntimeSettings.SetAsync("MQTT.Password", this.Password);
						await RuntimeSettings.SetAsync("MQTT.TrustServer", this.TrustCertificate);

						await this.mqtt.SUBSCRIBE("#");
					}
					catch (Exception ex)
					{
						Log.Critical(ex);
					}
				});
			}

			return Task.CompletedTask;
		}

		private Task Mqtt_OnContentReceived(object Sender, MqttContent Content)
		{
			this.LastTopic = Content.Topic;

			Application.Current.Dispatcher.Invoke(() =>
			{
				try
				{
					if (Content.Topic == this.SelectedTopic)
						this.Messages.Add(Content);

					if (!this.topics.TryGetValue(Content.Topic, out MqttTopic Topic))
					{
						string[] Parts = Content.Topic.Split('/');
						MqttTopic Last = null;
						string s = null;

						foreach (string Part in Parts)
						{
							if (s is null)
								s = Part;
							else
								s += "/" + Part;

							if (!this.topics.TryGetValue(s, out Topic))
							{
								Topic = new MqttTopic(s);
								this.topics[s] = Topic;

								if (Last is null)
									Insert(this.rootTopics, Topic);
								else
									Insert(Last.Items, Topic);
							}

							Last = Topic;
						}
					}
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
				}
			});

			return Task.CompletedTask;
		}

		private static void Insert<T>(ObservableCollection<T> Collection, T NewItem)
			where T : IComparer<T>
		{
			int i, j, c = Collection.Count;

			for (i = 0; i < c; i++)
			{
				j = NewItem.Compare(Collection[i], NewItem);
				if (j > 0)
					break;
			}

			if (i >= c)
				Collection.Add(NewItem);
			else
				Collection.Insert(i, NewItem);
		}

		/// <summary>
		/// Closes the connection to the broker.
		/// </summary>
		private void ExecuteClose()
		{
			this.mqtt?.Dispose();
			this.mqtt = null;
			this.State = MqttState.Offline;

			this.Connect.Changed();
		}

		/// <summary>
		/// Clears the window
		/// </summary>
		private void ExecuteClear()
		{
			this.topics.Clear();
			this.rootTopics.Clear();
		}

		/// <summary>
		/// Publishes content to the broker.
		/// </summary>
		private async void ExecutePublish()
		{
			if (!(this.mqtt is null))
			{
				try
				{
					await this.mqtt.PUBLISH(this.SelectedTopic, this.QoS, this.Retain, Encoding.UTF8.GetBytes(this.SelectedContent));
				}
				catch (Exception ex)
				{
					Log.Critical(ex);
					MessageBox.Show(ex.Message);
				}
			}
		}

		/// <summary>
		/// <see cref="IDisposable.Dispose"/>
		/// </summary>
		public void Dispose()
		{
			this.mqtt?.Dispose();
			this.mqtt = null;
		}
	}
}
