using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Waher.Content;
using Waher.Events;
using Waher.Events.Console;
using Waher.Events.MQTT;
using Waher.Networking.MQTT;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;
using Waher.Runtime.Settings;
using Waher.Security;
using Waher.Security.EllipticCurves;
using Waher.Things;
using Waher.Things.SensorData;

namespace Sensor
{
	/// <summary>
	/// This project implements a simple sensor that gets its values from the Internet and publishes it on an MQTT Broker. 
	/// It publishes the information in five different ways: Unstructured, Structured, Interoperable (first three unsecured), 
	/// and public, Confidential (last two secured).
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
			Edwards25519 Cipher;
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

				// Configuring Keys

				string p = await RuntimeSettings.GetAsync("ed25519.p", string.Empty);
				byte[] Secret;

				if (string.IsNullOrEmpty(p))
					Secret = null;
				else
				{
					try
					{
						Secret = Convert.FromBase64String(p);
					}
					catch (Exception)
					{
						Secret = null;
					}
				}

				if (Secret is null)
				{
					Log.Informational("Generating new keys.", DeviceID);

					Cipher = new Edwards25519();
					Secret = Cipher.GenerateSecret();
					p = Convert.ToBase64String(Secret);
					await RuntimeSettings.SetAsync("ed25519.p", p);
				}

				Cipher = new Edwards25519(Convert.FromBase64String(p));

				Log.Informational("Public key: " + Convert.ToBase64String(Cipher.PublicKey), DeviceID);

				// Checking pairing information

				string PairedTo = await RuntimeSettings.GetAsync("ed25519.pair", string.Empty);
				byte[] PairedToBin;

				if (string.IsNullOrEmpty(PairedTo))
					PairedToBin = null;
				else
				{
					try
					{
						PairedToBin = Convert.FromBase64String(PairedTo);
					}
					catch
					{
						PairedToBin = null;
					}
				}

				if (PairedToBin is null)
					Log.Informational("Not paired to any device.", DeviceID);
				else
					Log.Informational("Paired to: " + PairedTo, DeviceID);

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
						await ReportSensorData(SensorData, Mqtt, DeviceID, "Sensor", Cipher, PairedToBin);

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
						await ReportSensorData(SensorData, Mqtt, DeviceID, "Sensor", Cipher, PairedToBin);

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

				// Configure pairing

				if (PairedToBin is null)
				{
					Dictionary<int, string> NrToKey = new Dictionary<int, string>();
					Dictionary<string, int> KeyToNr = new Dictionary<string, int>();

					// Receiving public key of device ready to be paired.

					Mqtt.OnContentReceived += (sender, e) =>
					{
						if (e.Topic.StartsWith("HardenMqtt/Secured/Pairing/"))
						{
							lock (NrToKey)
							{
								string Key = e.Topic[27..];

								if (Key.Length < 100 && e.Data.Length < 100 && !KeyToNr.ContainsKey(Key))
								{
									try
									{
										byte[] KeyBin = Convert.FromBase64String(Key);
										Cipher.GetSharedKey(KeyBin, Hashes.ComputeSHA256Hash);
									}
									catch
									{
										return Task.CompletedTask;  // Invalid key
									}

									int KeyNr = NrToKey.Count + 1;
									NrToKey[KeyNr] = Key;
									KeyToNr[Key] = KeyNr;

									Log.Notice("Device ready to be paired: " + KeyNr + ". " + Encoding.UTF8.GetString(e.Data) + ": " + Key, DeviceID);
								}
							}
						}

						return Task.CompletedTask;
					};

					// Subscribe to pairing messages

					await Mqtt.SUBSCRIBE("HardenMqtt/Secured/Pairing/+");

					// Pair device

					while (PairedToBin is null)
					{
						PairedTo = UserInput("Public Key of remote device", PairedTo ?? string.Empty);

						try
						{
							if (int.TryParse(PairedTo, out int Nr))
							{
								lock (NrToKey)
								{
									if (NrToKey.TryGetValue(Nr, out string s))
										PairedTo = s;
								}
							}

							byte[] Bin = Convert.FromBase64String(PairedTo);
							Edwards25519 Temp = new Edwards25519(Bin);

							PairedToBin = Bin;
							await RuntimeSettings.SetAsync("ed25519.pair", PairedTo);
						}
						catch (Exception)
						{
							Log.Error("Invalid public key provided during pairing.", DeviceID);
						}
					}

					// Unsubscribe from pairing messages

					await Mqtt.UNSUBSCRIBE("HardenMqtt/Secured/Pairing/+");
				}

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
		/// <param name="DeviceID">Device ID</param>
		/// <param name="Cipher">Cipher to use for security purposes.</param>
		/// <param name="PairedPublicKey">Public Key of paired recipient.</param>
		private static async Task ReportSensorData(WeatherInformation SensorData, MqttClient Mqtt, string DeviceID, string DeviceType,
			EllipticCurve Cipher, byte[] PairedPublicKey)
		{
			await ReportSensorDataUnsecuredUnstructured(SensorData, Mqtt, "HardenMqtt/Unsecured/Unstructured/" + DeviceID);
			await ReportSensorDataUnsecuredStructured(SensorData, Mqtt, "HardenMqtt/Unsecured/Structured/" + DeviceID);
			await ReportSensorDataUnsecuredInteroperable(SensorData, Mqtt, "HardenMqtt/Unsecured/Interoperable/" + DeviceID, DeviceID);
			await ReportSensorDataSecuredPublic(SensorData, Mqtt, "HardenMqtt/Secured/Public/" + Convert.ToBase64String(Cipher.PublicKey), DeviceID, Cipher);

			if (PairedPublicKey is null)
				await PublishString(Mqtt, "HardenMqtt/Secured/Pairing/" + Convert.ToBase64String(Cipher.PublicKey), DeviceType, false);
			else
				await ReportSensorDataSecuredConfidential(SensorData, Mqtt, "HardenMqtt/Secured/Confidential/" + Convert.ToBase64String(Cipher.PublicKey), DeviceID, Cipher, PairedPublicKey);
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
			await PublishString(Mqtt, BaseTopic + "/Timestamp", SensorData.Timestamp.ToString(), true);
			await PublishString(Mqtt, BaseTopic + "/Name", SensorData.Name, true);
			await PublishString(Mqtt, BaseTopic + "/Id", SensorData.Id, true);
			await PublishString(Mqtt, BaseTopic + "/Country", SensorData.Country, true);
			await PublishString(Mqtt, BaseTopic + "/Weather", SensorData.Weather, true);
			await PublishString(Mqtt, BaseTopic + "/IconUrl", SensorData.IconUrl, true);
			await PublishString(Mqtt, BaseTopic + "/Description", SensorData.Description, true);
			await PublishString(Mqtt, BaseTopic + "/TimeZone", SensorData.TimeZone?.ToString() ?? string.Empty, true);
			await PublishString(Mqtt, BaseTopic + "/VisibilityMeters", (SensorData.VisibilityMeters?.ToString() ?? string.Empty, true) + " m", true);
			await PublishString(Mqtt, BaseTopic + "/Longitude", (SensorData.LongitudeDegrees?.ToString() ?? string.Empty) + "°", true);
			await PublishString(Mqtt, BaseTopic + "/Latitude", (SensorData.LatitudeDegrees?.ToString() ?? string.Empty) + "°", true);
			await PublishString(Mqtt, BaseTopic + "/Temperature", (SensorData.TemperatureCelcius?.ToString() ?? string.Empty) + "° C", true);
			await PublishString(Mqtt, BaseTopic + "/TemperatureMin", (SensorData.TemperatureMinCelcius?.ToString() ?? string.Empty) + "° C", true);
			await PublishString(Mqtt, BaseTopic + "/TemperatureMax", (SensorData.TemperatureMaxCelcius?.ToString() ?? string.Empty) + "° C", true);
			await PublishString(Mqtt, BaseTopic + "/FeelsLike", (SensorData.FeelsLikeCelcius?.ToString() ?? string.Empty) + "° C", true);
			await PublishString(Mqtt, BaseTopic + "/Pressure", (SensorData.PressureHPa?.ToString() ?? string.Empty) + " hPa", true);
			await PublishString(Mqtt, BaseTopic + "/Humidity", (SensorData.HumidityPercent?.ToString() ?? string.Empty) + "%", true);
			await PublishString(Mqtt, BaseTopic + "/WindSpeed", (SensorData.WindSpeedMPerS?.ToString() ?? string.Empty) + " m/s", true);
			await PublishString(Mqtt, BaseTopic + "/WindDirection", (SensorData.WindDirectionDegrees?.ToString() ?? string.Empty) + "°", true);
			await PublishString(Mqtt, BaseTopic + "/Cloudiness", (SensorData.CloudinessPercent?.ToString() ?? string.Empty) + "%", true);
			await PublishString(Mqtt, BaseTopic + "/WeatherId", SensorData.WeatherId?.ToString() ?? string.Empty, true);
			await PublishString(Mqtt, BaseTopic + "/Sunrise", SensorData.Sunrise.ToString(), true);
			await PublishString(Mqtt, BaseTopic + "/Sunset", SensorData.Sunset.ToString(), true);
		}

		/// <summary>
		/// Publishes sensor data to MQTT in an unsecure, but structured manner.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic</param>
		private static async Task ReportSensorDataUnsecuredStructured(WeatherInformation SensorData, MqttClient Mqtt, string Topic)
		{
			string Json = JSON.Encode(SensorData, false);
			await PublishString(Mqtt, Topic, Json, true);
		}

		/// <summary>
		/// Publishes sensor data to MQTT in an unsecure, but interoperable manner.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic</param>
		private static async Task ReportSensorDataUnsecuredInteroperable(WeatherInformation SensorData, MqttClient Mqtt,
			string Topic, string DeviceID)
		{
			string Xml = GetInteroperableXml(SensorData, DeviceID, null);
			await PublishString(Mqtt, Topic, Xml, true);
		}

		/// <summary>
		/// Generates interoperable XML in a loosely coupled format.
		/// </summary>
		/// <param name="SensorData">Sensor Data</param>
		/// <param name="DeviceID">Device ID</param>
		/// <param name="Signature">Optional Cryptgraphic Signature of contents.</param>
		/// <returns>Interoperable XML</returns>
		private static string GetInteroperableXml(WeatherInformation SensorData, string DeviceID, byte[] Signature)
		{
			List<Field> Result = new List<Field>();
			ThingReference Ref = new ThingReference(DeviceID);

			if (!string.IsNullOrEmpty(SensorData.Name))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "Name", SensorData.Name,
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (!string.IsNullOrEmpty(SensorData.Id))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "ID", SensorData.Id,
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (SensorData.TimeZone.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Time Zone",
					SensorData.TimeZone.Value / 3600.0, 2, "h", FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (SensorData.VisibilityMeters.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Visibility",
					SensorData.VisibilityMeters.Value, 0, "m", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.LongitudeDegrees.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Longitude",
					SensorData.LongitudeDegrees.Value, 2, "°", FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (SensorData.LatitudeDegrees.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Latitude",
					SensorData.LatitudeDegrees.Value, 2, "°", FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (SensorData.TemperatureCelcius.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Temperature",
					SensorData.TemperatureCelcius.Value, 2, "°C", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.FeelsLikeCelcius.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Feels Like",
					SensorData.FeelsLikeCelcius.Value, 2, "°C", FieldType.Computed, FieldQoS.AutomaticReadout));
			}

			if (SensorData.TemperatureMinCelcius.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Temperature, Min",
					SensorData.TemperatureMinCelcius.Value, 2, "°C", FieldType.Peak, FieldQoS.AutomaticReadout));
			}

			if (SensorData.TemperatureMaxCelcius.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Temperature, Max",
					SensorData.TemperatureMaxCelcius.Value, 2, "°C", FieldType.Peak, FieldQoS.AutomaticReadout));
			}

			if (SensorData.PressureHPa.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Pressure",
					SensorData.PressureHPa.Value, 0, "hPa", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.HumidityPercent.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Humidity",
					SensorData.HumidityPercent.Value, 0, "%", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.WindSpeedMPerS.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Wind, Speed",
					SensorData.WindSpeedMPerS.Value, 1, "m/s", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.WindDirectionDegrees.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Wind, Direction",
					SensorData.WindDirectionDegrees.Value, 0, "°", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.CloudinessPercent.HasValue)
			{
				Result.Add(new QuantityField(Ref, SensorData.Timestamp, "Cloudiness",
					SensorData.CloudinessPercent.Value, 0, "%", FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.WeatherId.HasValue)
			{
				Result.Add(new Int32Field(Ref, SensorData.Timestamp, "Weather, ID",
					SensorData.WeatherId.Value, FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (!string.IsNullOrEmpty(SensorData.Country))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "Country", SensorData.Country,
					FieldType.Identity, FieldQoS.AutomaticReadout));
			}

			if (SensorData.Sunrise.HasValue)
			{
				Result.Add(new DateTimeField(Ref, SensorData.Timestamp, "Sunrise",
					SensorData.Sunrise.Value, FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (SensorData.Sunset.HasValue)
			{
				Result.Add(new DateTimeField(Ref, SensorData.Timestamp, "Sunset",
					SensorData.Sunset.Value, FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (!string.IsNullOrEmpty(SensorData.Weather))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "Weather", SensorData.Weather,
					FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (!string.IsNullOrEmpty(SensorData.Description))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "Weather, Description",
					SensorData.Description, FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (!string.IsNullOrEmpty(SensorData.IconUrl))
			{
				Result.Add(new StringField(Ref, SensorData.Timestamp, "Weather, Icon",
					SensorData.IconUrl, FieldType.Momentary, FieldQoS.AutomaticReadout));
			}

			if (!(Signature is null))
			{
				Result.Add(new StringField(Ref, DateTime.Now, "Signature",
					Convert.ToBase64String(Signature), FieldType.Computed, FieldQoS.AutomaticReadout));
			}

			SensorData Fields = new SensorData(Result);

			return Fields.PayloadXml;
		}

		/// <summary>
		/// Publishes sensor data to MQTT in a secure, but public.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic</param>
		/// <param name="Cipher">Cipher to use to generate signature.</param>
		private static async Task ReportSensorDataSecuredPublic(WeatherInformation SensorData, MqttClient Mqtt,
			string Topic, string DeviceID, EllipticCurve Cipher)
		{
			string Xml = GetInteroperableXml(SensorData, DeviceID, null);
			byte[] Bin = Encoding.UTF8.GetBytes(Xml);
			byte[] Signature = Cipher.Sign(Bin);

			Xml = GetInteroperableXml(SensorData, DeviceID, Signature);

			await PublishString(Mqtt, Topic, Xml, true);
		}

		/// <summary>
		/// Publishes sensor data to MQTT in an unsecure, but structured manner.
		/// </summary>
		/// <param name="SensorData">Collected Sensor Data</param>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic</param>
		/// <param name="Cipher">Cipher to use to generate signature.</param>
		private static async Task ReportSensorDataSecuredConfidential(WeatherInformation SensorData, MqttClient Mqtt,
			string Topic, string DeviceID, EllipticCurve Cipher, byte[] RemotePublicKey)
		{
			string Xml = GetInteroperableXml(SensorData, DeviceID, null);
			byte[] Bin = Encoding.UTF8.GetBytes(Xml);
			byte[] Signature = Cipher.Sign(Bin);

			Xml = GetInteroperableXml(SensorData, DeviceID, Signature);
			Bin = Encoding.UTF8.GetBytes(Xml);

			byte[] Key = Cipher.GetSharedKey(RemotePublicKey, Hashes.ComputeSHA256Hash);
			byte[] Nonce = new byte[16];
			byte[] IV = new byte[16];

			rnd.GetBytes(Nonce);
			rnd.GetBytes(IV);

			using Aes Aes = Aes.Create();
			Aes.BlockSize = 128;
			Aes.KeySize = 256;
			Aes.Mode = CipherMode.CBC;
			Aes.Padding = PaddingMode.PKCS7;

			using ICryptoTransform Encryptor = Aes.CreateEncryptor(Key, IV);
			byte[] Encrypted = Encryptor.TransformFinalBlock(Bin, 0, Bin.Length);
			byte[] ToSend = new byte[Encrypted.Length + 32];
			Array.Copy(IV, 0, ToSend, 0, 16);       // These are not secret. Only used to create entropy, to assure not the same information and parameters are used in different messages.
			Array.Copy(Nonce, 0, ToSend, 16, 16);   // These are not secret. Only used to create entropy, to assure not the same information and parameters are used in different messages.
			Array.Copy(Encrypted, 0, ToSend, 32, Encrypted.Length);

			await Mqtt.PUBLISH(Topic, MqttQualityOfService.AtMostOnce, true, ToSend);
		}

		private static readonly RandomNumberGenerator rnd = RandomNumberGenerator.Create();

		/// <summary>
		/// Encodes a string (using UTF-8) and publishes the binary encoding to a topic on MQTT, using at most once QoS.
		/// </summary>
		/// <param name="Mqtt">Connected MQTT Client</param>
		/// <param name="Topic">Topic to publish to</param>
		/// <param name="Value">String value to encode and publish.</param>
		/// <param name="Retain">If value should be retained.</param>
		private static async Task PublishString(MqttClient Mqtt, string Topic, string Value, bool Retain)
		{
			byte[] Binary = Encoding.UTF8.GetBytes(Value);
			await Mqtt.PUBLISH(Topic, MqttQualityOfService.AtMostOnce, Retain, Binary);
		}

		#endregion
	}
}
