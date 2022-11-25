using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Waher.Content;
using Waher.Events;
using Waher.Events.Console;
using Waher.Events.MQTT;
using Waher.Networking.MQTT;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;
using Waher.Runtime.Settings;

namespace Sensor
{
	/// <summary>
	/// This project implements a simple sensor that gets its values from the Internet and publishes it on an MQTT Broker. 
	/// It publishes the information in four different ways: Unsecure and Unstructured, Unsecure and Structured, 
	/// Secured but public, Secured and Confidential.
	/// </summary>
	internal class Program
	{
		/// <summary>
		/// Program entry point
		/// </summary>
		static async Task Main()
		{
			FilesProvider DBProvider = null;
			OpenWeatherMapApi Api = null;
			MqttClient Mqtt = null;
			Timer Timer = null;
			string DeviceID = string.Empty;

			try
			{
				// First, initialize environment and type inventory. This creates an inventory of types used by the application.
				// This is important for tasks such as data persistence, for example.
				TypesLoader.Initialize();

				// Setup Event Output to Console window
				Log.Register(new ConsoleEventSink());
				Log.Informational("Sensor application starting...");

				// Setup database
				Log.Informational("Setting up database...");
				DBProvider = await FilesProvider.CreateAsync("Database", "Default", 8192, 10000, 8192, Encoding.UTF8, 10000, true, false);
				Database.Register(DBProvider);

				// Repair database, if an inproper shutdown is detected
				await DBProvider.RepairIfInproperShutdown(string.Empty);

				// Starting internal modules
				await Types.StartAllModules(60000);

				// Configuring Device ID

				DeviceID = await RuntimeSettings.GetAsync("Device.ID", string.Empty);

				if (string.IsNullOrEmpty(DeviceID))
				{
					Console.Out.WriteLine("Device ID has not been configured. Please provide the Device ID that will be used by this application.");

					DeviceID = UserInput("Device ID", DeviceID);
					await RuntimeSettings.SetAsync("Device.ID", DeviceID);
				}
				else
					Log.Informational("Using Device ID: " + DeviceID, DeviceID);

				// Configuring and connecting to MQTT Server

				bool MqttConnected = false;

				do
				{
					// MQTT Configuration

					string MqttHost = await RuntimeSettings.GetAsync("MQTT.Host", string.Empty);
					int MqttPort = (int)await RuntimeSettings.GetAsync("MQTT.Port", 8883);
					bool MqttEncrypted = await RuntimeSettings.GetAsync("MQTT.Tls", true);
					string MqttUserName = await RuntimeSettings.GetAsync("MQTT.UserName", string.Empty);
					string MqttPassword = await RuntimeSettings.GetAsync("MQTT.Password", string.Empty);

					if (string.IsNullOrEmpty(MqttHost))
					{
						Console.Out.WriteLine("MQTT host not configured. Please provide the connection details below. If the default value presented is sufficient, just press ENTER.");

						MqttHost = UserInput("MQTT Host", MqttHost);
						await RuntimeSettings.SetAsync("MQTT.Host", MqttHost);

						MqttPort = UserInput("MQTT Port", MqttPort, 1, 65535);
						await RuntimeSettings.SetAsync("MQTT.Port", MqttPort);

						MqttEncrypted = (MqttPort == 8883);
						MqttEncrypted = UserInput("Encrypt with TLS", MqttEncrypted);
						await RuntimeSettings.SetAsync("MQTT.Tls", MqttEncrypted);

						MqttUserName = UserInput("MQTT UserName", MqttUserName);
						await RuntimeSettings.SetAsync("MQTT.UserName", MqttUserName);

						MqttPassword = UserInput("MQTT Password", MqttPassword);
						await RuntimeSettings.SetAsync("MQTT.Password", MqttPassword);
					}

					if (!(Mqtt is null))
					{
						await Mqtt.DisposeAsync();
						Mqtt = null;
					}

					// Connecting to broker and waiting for connection to complete

					Log.Informational("Connecting to MQTT Broker...", DeviceID);

					TaskCompletionSource<bool> WaitForConnect = new TaskCompletionSource<bool>();

					Mqtt = new MqttClient(MqttHost, MqttPort, MqttEncrypted, MqttUserName, MqttPassword);

					Mqtt.OnConnectionError += (_, e) =>
					{
						Log.Error(e.Message, DeviceID);
						WaitForConnect.TrySetResult(false);
						return Task.CompletedTask;
					};

					Mqtt.OnError += (_, e) =>
					{
						Log.Error(e.Message, DeviceID);
						return Task.CompletedTask;
					};

					Mqtt.OnStateChanged += (_, NewState) =>
					{
						Log.Informational(NewState.ToString(), DeviceID);

						switch (NewState)
						{
							case MqttState.Offline:
							case MqttState.Error:
								WaitForConnect.TrySetResult(false);
								break;

							case MqttState.Connected:
								WaitForConnect.TrySetResult(true);
								break;
						}

						return Task.CompletedTask;
					};

					MqttConnected = await WaitForConnect.Task;
				}
				while (!MqttConnected);

				// Register MQTT event sink, allowing developer to follow what happens with devices.

				Log.Register(new MqttEventSink("MQTT Event Sink", Mqtt, "HardenMqtt/Events", true));
				Log.Informational("Sensor connected to MQTT.", DeviceID);

				// Configure and setup sensor
				// For this example, we use weather data from Open Weather Map.
				// You will need an API Key for this. You can get one here: https://openweathermap.org/api

				bool ApiConnected = false;

				do
				{
					string ApiKey = await RuntimeSettings.GetAsync("API.Key", string.Empty);
					string ApiLocation = await RuntimeSettings.GetAsync("API.Location", "Viña del Mar");
					string ApiCountry = await RuntimeSettings.GetAsync("API.Country", "CL");

					if (string.IsNullOrEmpty(ApiKey))
					{
						Console.Out.WriteLine("Open Weather Map API connection not configured. Please provide the connection details below. If the default value presented is sufficient, just press ENTER. You can get an API Key here: You can get one here: https://openweathermap.org/api");

						ApiKey = UserInput("API Key", ApiKey);
						await RuntimeSettings.SetAsync("API.Key", ApiKey);

						ApiLocation = UserInput("API Location", ApiLocation);
						await RuntimeSettings.SetAsync("API.Location", ApiLocation);

						ApiCountry = UserInput("API Country Code", ApiCountry);
						await RuntimeSettings.SetAsync("API.Country", ApiCountry);
					}

					Log.Informational("Connecting to API...", DeviceID);

					try
					{
						Api = new OpenWeatherMapApi(ApiKey, ApiLocation, ApiCountry);

						WeatherInformation SensorData = await Api.GetData();
						await ReportSensorData(SensorData, Mqtt, DeviceID);

						ApiConnected = true;
						Log.Informational("Sensor connected to API.", DeviceID);
					}
					catch (Exception ex)
					{
						Log.Error(ex.Message, DeviceID);
					}
				}
				while (!ApiConnected);

				// Schedule regular sensor data readouts

				Timer = new Timer(async (_) =>
				{
					try
					{
						Log.Informational("Reading weather information.", DeviceID);

						WeatherInformation SensorData = await Api.GetData();
						await ReportSensorData(SensorData, Mqtt, DeviceID);

						Log.Informational("Weather data read. Publishing to MQTT.", DeviceID);
					}
					catch (Exception ex)
					{
						Log.Critical(ex, DeviceID);
					}
				}, null, 1000, 60000);  // First readout in 1s, then read every 1 minute.

				// Configure CTRL+Z to close application gracefully.

				bool Continue = true;

				Console.CancelKeyPress += (_, e) =>
				{
					e.Cancel = true;
					Continue = false;
				};

				// Normal operation

				Log.Informational("Sensor application started... Press CTRL+C to terminate the application.", DeviceID);

				while (Continue)
					await Task.Delay(100);
			}
			catch (Exception ex)
			{
				// Display exception terminating application
				Log.Alert(ex, DeviceID);
			}
			finally
			{
				// Shut down database gracefully

				Log.Informational("Sensor application stopping...", DeviceID);

				Timer?.Dispose();
				Timer = null;

				if (!(Mqtt is null))
				{
					await Mqtt.DisposeAsync();
					Mqtt = null;
				}

				if (!(DBProvider is null))
					await DBProvider.Flush();

				await Types.StopAllModules();
				Log.Terminate();
			}
		}

		#region UserInput methods

		/// <summary>
		/// Asks the user to input a string.
		/// </summary>
		/// <param name="Label">Label</param>
		/// <param name="Default">Default value</param>
		/// <returns>User input</returns>
		private static string UserInput(string Label, string Default)
		{
			Console.Out.Write(Label);

			if (!string.IsNullOrEmpty(Default))
			{
				Console.Out.Write(" (default: ");
				Console.Out.Write(Default);
				Console.Out.Write(")");
			}

			Console.Out.Write(": ");

			string s = Console.In.ReadLine();

			if (string.IsNullOrEmpty(s))
				return Default;
			else
				return s;
		}

		/// <summary>
		/// Asks the user to input a Boolean value.
		/// </summary>
		/// <param name="Label">Label</param>
		/// <param name="Default">Default value</param>
		/// <returns>User input</returns>
		private static bool UserInput(string Label, bool Default)
		{
			string s;
			bool Result;

			do
			{
				s = UserInput(Label, Default.ToString());
			}
			while (!bool.TryParse(s, out Result));

			return Result;
		}

		/// <summary>
		/// Asks the user to input a 32-bit integer.
		/// </summary>
		/// <param name="Label">Label</param>
		/// <param name="Default">Default value</param>
		/// <param name="Min">Smallest permitted value.</param>
		/// <param name="Max">Largest permitted value.</param>
		/// <returns>User input</returns>
		private static int UserInput(string Label, int Default, int Min, int Max)
		{
			string s;
			int Result;

			do
			{
				s = UserInput(Label, Default.ToString());
			}
			while (!int.TryParse(s, out Result) || Result < Min || Result > Max);

			return Result;
		}

		#endregion

		#region Publish sensor data

		/// <summary>
		/// Publishes sensor data to MQTT
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		private static async Task ReportSensorData(WeatherInformation SensorData, MqttClient Mqtt, string DeviceID)
		{
			await ReportSensorDataUnsecuredUnstructured(SensorData, Mqtt, "HardenMqtt/Unstructured/" + DeviceID);
			await ReportSensorDataUnsecuredStructured(SensorData, Mqtt, "HardenMqtt/Structured/" + DeviceID);
		}

		/// <summary>
		/// Publishes sensor data to MQTT in an unsecure, and unstructured manner.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="BaseTopic">Base Topic</param>
		private static async Task ReportSensorDataUnsecuredUnstructured(WeatherInformation SensorData, MqttClient Mqtt,
			string BaseTopic)
		{
			await PublishString(Mqtt, BaseTopic + "/Timestamp", SensorData.Timestamp.ToString());
			await PublishString(Mqtt, BaseTopic + "/Name", SensorData.Name);
			await PublishString(Mqtt, BaseTopic + "/Id", SensorData.Id);
			await PublishString(Mqtt, BaseTopic + "/Country", SensorData.Country);
			await PublishString(Mqtt, BaseTopic + "/Weather", SensorData.Weather);
			await PublishString(Mqtt, BaseTopic + "/IconUrl", SensorData.IconUrl);
			await PublishString(Mqtt, BaseTopic + "/Description", SensorData.Description);
			await PublishString(Mqtt, BaseTopic + "/TimeZone", SensorData.TimeZone?.ToString() ?? string.Empty);
			await PublishString(Mqtt, BaseTopic + "/VisibilityMeters", (SensorData.VisibilityMeters?.ToString() ?? string.Empty) + " m");
			await PublishString(Mqtt, BaseTopic + "/Longitude", (SensorData.LongitudeDegrees?.ToString() ?? string.Empty) + "°");
			await PublishString(Mqtt, BaseTopic + "/Latitude", (SensorData.LatitudeDegrees?.ToString() ?? string.Empty) + "°");
			await PublishString(Mqtt, BaseTopic + "/Temperature", (SensorData.TemperatureCelcius?.ToString() ?? string.Empty) + "° C");
			await PublishString(Mqtt, BaseTopic + "/TemperatureMin", (SensorData.TemperatureMinCelcius?.ToString() ?? string.Empty) + "° C");
			await PublishString(Mqtt, BaseTopic + "/TemperatureMax", (SensorData.TemperatureMaxCelcius?.ToString() ?? string.Empty) + "° C");
			await PublishString(Mqtt, BaseTopic + "/FeelsLike", (SensorData.FeelsLikeCelcius?.ToString() ?? string.Empty) + "° C");
			await PublishString(Mqtt, BaseTopic + "/Pressure", (SensorData.PressureHPa?.ToString() ?? string.Empty) + " hPa");
			await PublishString(Mqtt, BaseTopic + "/Humidity", (SensorData.HumidityPercent?.ToString() ?? string.Empty) + "%");
			await PublishString(Mqtt, BaseTopic + "/WindSpeed", (SensorData.WindSpeedMPerS?.ToString() ?? string.Empty) + " m/s");
			await PublishString(Mqtt, BaseTopic + "/WindDirection", (SensorData.WindDirectionDegrees?.ToString() ?? string.Empty) + "°");
			await PublishString(Mqtt, BaseTopic + "/Cloudiness", (SensorData.CloudinessPercent?.ToString() ?? string.Empty) + "%");
			await PublishString(Mqtt, BaseTopic + "/WeatherId", SensorData.WeatherId?.ToString() ?? string.Empty);
			await PublishString(Mqtt, BaseTopic + "/Sunrise", SensorData.Sunrise.ToString());
			await PublishString(Mqtt, BaseTopic + "/Sunset", SensorData.Sunset.ToString());
		}

		/// <summary>
		/// Publishes sensor data to MQTT in an unsecure, but structured manner.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="BaseTopic">Base Topic</param>
		private static async Task ReportSensorDataUnsecuredStructured(WeatherInformation SensorData, MqttClient Mqtt,
			string BaseTopic)
		{
			string Json = JSON.Encode(SensorData, false);
			await PublishString(Mqtt, BaseTopic, Json);
		}

		/// <summary>
		/// Encodes a string (using UTF-8) and publishes the binary encoding to a topic on MQTT, using at most once QoS.
		/// </summary>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic to publish to</param>
		/// <param name="Value">String value to encode and publish.</param>
		private static async Task PublishString(MqttClient Mqtt, string Topic, string Value)
		{
			byte[] Binary = Encoding.UTF8.GetBytes(Value);
			await Mqtt.PUBLISH(Topic, MqttQualityOfService.AtMostOnce, true, Binary);
		}

		#endregion
	}
}
