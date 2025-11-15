using Pairing;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
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
using Waher.Runtime.Queue;
using Waher.Runtime.Settings;
using Waher.Security.EllipticCurves;
using Waher.Security.SHA3;
using Waher.Things.SensorData;

namespace Display
{
	/// <summary>
	/// This project implements a simple display that gets its values from information published via an MQTT Broker. 
	/// Information can be received in five different ways: Unstructured, Structured, Interoperable (first three unsecured), 
	/// and public, Confidential (last two secured).
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
			Edwards25519 Cipher;
			string DeviceID = string.Empty;

			try
			{
				#region Setup

				// First, initialize environment and type inventory. This creates an inventory of types used by the application.
				// This is important for tasks such as data persistence, for example.
				TypesLoader.Initialize();

				// Exception types that are logged with an elevated type.
				Log.RegisterAlertExceptionType(true,
					typeof(OutOfMemoryException),
					typeof(StackOverflowException),
					typeof(AccessViolationException),
					typeof(InsufficientMemoryException));

				// Setup Event Output to Console window
				Log.Register(new ConsoleEventSink());
				Log.Informational("Display application starting...");

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

				#region Keys

				// Configuring Keys

				string p = await RuntimeSettings.GetAsync("ed25519.p", string.Empty);
				byte[] Secret;

				if (string.IsNullOrEmpty(p))
					Secret = null;
				else
				{
					try
					{
						Secret = Base64Url.Decode(p);   // Note: Use BAS64URL encoding instead of BASE64, to avoid path characters in topics.
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
					p = Base64Url.Encode(Secret);
					await RuntimeSettings.SetAsync("ed25519.p", p);
				}

				Cipher = new Edwards25519(Base64Url.Decode(p));

				Log.Informational("Public key: " + Base64Url.Encode(Cipher.PublicKey), DeviceID);

				#endregion

				#region Loading Pairing Information

				// Checking pairing information

				string PairedToKey = await RuntimeSettings.GetAsync("Pair.Ed25519.Public", string.Empty);
				string PairedToId = await RuntimeSettings.GetAsync("Pair.Id", string.Empty);
				byte[] PairedToKeyBin;

				if (string.IsNullOrEmpty(PairedToKey))
					PairedToKeyBin = null;
				else
				{
					try
					{
						PairedToKeyBin = Base64Url.Decode(PairedToKey);
					}
					catch
					{
						PairedToKeyBin = null;
					}
				}

				if (PairedToKeyBin is null)
					Log.Informational("Not paired to any device.", DeviceID);
				else
					Log.Informational("Paired to: " + PairedToKey + " (" + PairedToId + ")", DeviceID);

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

					if (Mqtt is not null)
					{
						await Mqtt.DisposeAsync();
						Mqtt = null;
					}

					// Connecting to broker and waiting for connection to complete

					Log.Informational("Connecting to MQTT Broker...", DeviceID);

					TaskCompletionSource<bool> WaitForConnect = new();

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
				Log.Informational("Display connected to MQTT.", DeviceID);

				// Configure CTRL+C to close application gracefully.

				#endregion

				#region CTRL-C support

				CancellationTokenSource Operation = new();
				AsyncQueue<MqttContent> InputQueue = new();

				Console.CancelKeyPress += (_, e) =>
				{
					e.Cancel = true;
					Operation.Cancel();
					InputQueue.Queue(null);
				};

				#endregion

				#region Pairing

				// Configure pairing

				if (PairedToKeyBin is null)
				{
					PairingInformation Info = await DevicePairing.PairDevice(Mqtt, Cipher, DeviceID,
						"Display", "Sensor", GetRandomBytes(32), true, Operation.Token);

					if (Info is not null)
					{
						PairedToKey = Info.SlavePublicKey;
						PairedToId = Info.SlaveId;

						await RuntimeSettings.SetAsync("Pair.Ed25519.Public", PairedToKey);
						await RuntimeSettings.SetAsync("Pair.Id", PairedToId);
					}
				}

				#endregion

				#region Receiving Sensor Data

				Mqtt.OnContentReceived += (sender, e) =>
				{
					InputQueue.Queue(e);
					return Task.CompletedTask;
				};

				int DisplayMode = 0;

				string MenuTopic(int Menu)
				{
					return Menu switch
					{
						1 => "HardenMqtt/Unsecured/Unstructured/" + PairedToId + "/+",
						2 => "HardenMqtt/Unsecured/Structured/" + PairedToId,
						3 => "HardenMqtt/Unsecured/Interoperable/" + PairedToId,
						4 => "HardenMqtt/Secured/Public/" + PairedToKey,
						5 => "HardenMqtt/Secured/Confidential/" + PairedToKey,
						_ => string.Empty,
					};
				};

				async Task ChangeMenu(int Menu)
				{
					if (DisplayMode != Menu)
					{
						string Topic = MenuTopic(DisplayMode);

						if (!string.IsNullOrEmpty(Topic))
						{
							Log.Informational("Unsubscribing from " + Topic, DeviceID);
							await Mqtt.UNSUBSCRIBE(Topic);
						}

						Topic = MenuTopic(Menu);

						if (!string.IsNullOrEmpty(Topic))
						{
							Log.Informational("Subscribing to " + Topic, DeviceID);
							await Mqtt.SUBSCRIBE(Topic);
						}

						DisplayMode = Menu;
					}
				};

				await ChangeMenu(1);

				#endregion

				#region Main loop

				Log.Informational("Display application started... Press CTRL+C to terminate the application.", DeviceID);

				Dictionary<string, int> RowPerField = [];
				DateTime Last = DateTime.MinValue;
				DateTime Current;

				// Make sure the main loop can check the keyboard often, to switch display mode.
				using Timer CheckKeyboardTimer = new((_) => InputQueue.Queue(null), null, 100, 100);

				while (!Operation.IsCancellationRequested)
				{
					if (Console.KeyAvailable)
					{
						ConsoleKeyInfo Key = Console.ReadKey(true);

						if (Key.KeyChar >= '1' && Key.KeyChar <= '5')
						{
							await ChangeMenu(Key.KeyChar - '0');
							ShowMenu(DisplayMode, RowPerField);
						}
						else if (Key.Key >= ConsoleKey.F1 && Key.Key <= ConsoleKey.F5)
						{
							await ChangeMenu(Key.Key - ConsoleKey.F1 + 1);
							ShowMenu(DisplayMode, RowPerField);
						}
						else
							Console.Beep();
					}

					MqttContent e = await InputQueue.Wait();
					if (e is null)
						continue;

					string[] Parts = e.Topic.Split('/');

					if (Parts.Length >= 4 && Parts[0] == "HardenMqtt")
					{
						try
						{
							Current = DateTime.Now;

							int? RedrawMenu;

							if (Current.Subtract(Last).TotalSeconds > 5)
								RedrawMenu = DisplayMode;
							else
								RedrawMenu = null;

							Last = Current;

							if (Parts[1] == "Unsecured")
							{
								if (Parts[3] == PairedToId)
								{
									switch (Parts[2])
									{
										case "Unstructured":        // Unstructured reception (unsecured)
											if (DisplayMode == 1)
												UnstructuredDataReceived(e.Topic, e.Data, RedrawMenu, RowPerField);
											break;

										case "Structured":          // Structured reception (unsecured)
											if (DisplayMode == 2)
												StructuredDataReceived(e.Data, RedrawMenu, RowPerField);
											break;

										case "Interoperable":       // Interoperable reception (unsecured)
											if (DisplayMode == 3)
												InteroperableDataReceived(e.Data, RedrawMenu, RowPerField);
											break;
									}
								}
							}
							else if (Parts[1] == "Secured")
							{
								if (Parts[3] == PairedToKey)
								{
									switch (Parts[2])
									{
										case "Public":              // Interoperable, signed, public reception (secured)
											if (DisplayMode == 4)
												InteroperableSignedPublicDataReceived(e.Data, Cipher, PairedToKeyBin, RedrawMenu, RowPerField);
											break;

										case "Confidential":        // Interoperable, signed, confidential reception (secured)
											if (DisplayMode == 5)
												InteroperableConfidentialDataReceived(e.Data, Cipher, PairedToKeyBin, RedrawMenu, RowPerField);
											break;
									}
								}
							}
						}
						catch (Exception ex)
						{
							Log.Exception(ex);
						}
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

				Log.Informational("Display application stopping...", DeviceID);

				if (Mqtt is not null)
				{
					await Mqtt.DisposeAsync();
					Mqtt = null;
				}

				if (DBProvider is not null)
					await DBProvider.Flush();

				await Types.StopAllModules();
				await Log.TerminateAsync();
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

		#region Data Publication

		private static byte[] GetRandomBytes(int N)
		{
			byte[] Result = new byte[N];
			rnd.GetBytes(Result);
			return Result;
		}

		private static readonly RandomNumberGenerator rnd = RandomNumberGenerator.Create();

		#endregion

		#region Data Presentation

		private static void ShowMenu(int DisplayMode, Dictionary<string, int> RowPerField)
		{
			Console.Clear();
			RowPerField.Clear();

			Print("1. Unstructured", 20, DisplayMode == 1, TextAlignment.Center);
			Print("2. Structured", 20, DisplayMode == 2, TextAlignment.Center);
			Print("3. Interoperable", 20, DisplayMode == 3, TextAlignment.Center);
			Print("4. Signed", 20, DisplayMode == 4, TextAlignment.Center);
			Print("5. Confidential", 20, DisplayMode == 5, TextAlignment.Center);
			Print("CTRL+C. Quit", 20, TextAlignment.Center);

			Console.Out.WriteLine();
		}

		private static void Print(string s, int MaxLen, bool Selected, TextAlignment Alignment)
		{
			if (Selected)
				Print(s, MaxLen, ConsoleColor.White, ConsoleColor.Blue, Alignment);
			else
				Print(s, MaxLen, ConsoleColor.White, ConsoleColor.Black, Alignment);
		}

		private static void Print(string s, int MaxLen, ConsoleColor FgColor, ConsoleColor BgColor, TextAlignment Alignment)
		{
			ConsoleColor FgBak = Console.ForegroundColor;
			ConsoleColor BgBak = Console.BackgroundColor;

			Console.ForegroundColor = FgColor;
			Console.BackgroundColor = BgColor;

			Print(s, MaxLen, Alignment);

			Console.ForegroundColor = FgBak;
			Console.BackgroundColor = BgBak;
		}

		public enum TextAlignment
		{
			Left,
			Center,
			Right
		}

		private static void Print(string s, int MaxLen, TextAlignment Alignment)
		{
			int i = s.Length;

			if (i > MaxLen)
				Console.Out.Write(s[0..MaxLen]);
			else
			{
				int NrLeft;
				int NrRight;

				switch (Alignment)
				{
					case TextAlignment.Left:
					default:
						NrLeft = 0;
						NrRight = MaxLen - i;
						break;

					case TextAlignment.Right:
						NrLeft = MaxLen - i;
						NrRight = 0;
						break;

					case TextAlignment.Center:
						NrLeft = (MaxLen - i) / 2;
						NrRight = (MaxLen - i) - NrLeft;
						break;

				}

				if (NrLeft > 0)
					Console.Out.Write(new string(' ', NrLeft));

				Console.Out.Write(s);

				if (NrRight > 0)
					Console.Out.Write(new string(' ', NrRight));
			}
		}

		private static void PrintField(string Key, string Value, Dictionary<string, int> RowPerField)
		{
			if (RowPerField.TryGetValue(Key, out int Row))
			{
				int RowBak = Console.CursorTop;

				Console.CursorTop = Row;
				Console.CursorLeft = 0;

				Print(Key, 25, ConsoleColor.White, ConsoleColor.Black, TextAlignment.Left);
				Print(Value, 50, ConsoleColor.White, ConsoleColor.DarkGray, TextAlignment.Right);

				Console.CursorTop = RowBak;
				Console.CursorLeft = 0;
			}
			else
			{
				RowPerField[Key] = Console.CursorTop;

				Print(Key, 25, ConsoleColor.White, ConsoleColor.Black, TextAlignment.Left);
				Print(Value ?? string.Empty, 50, ConsoleColor.White, ConsoleColor.DarkGray, TextAlignment.Right);

				Console.Out.WriteLine();
			}
		}

		private static void UnstructuredDataReceived(string Topic, byte[] Data, int? RedrawMenu,
			Dictionary<string, int> RowPerField)
		{
			if (RedrawMenu.HasValue)
				ShowMenu(RedrawMenu.Value, RowPerField);

			int i = Topic.LastIndexOf('/');
			if (i > 0)
				Topic = Topic[(i + 1)..];

			PrintField(Topic, Encoding.UTF8.GetString(Data), RowPerField);
		}

		private static void StructuredDataReceived(byte[] Data, int? RedrawMenu, Dictionary<string, int> RowPerField)
		{
			if (Data.Length > 65536)
				return;

			if (RedrawMenu.HasValue)
				ShowMenu(RedrawMenu.Value, RowPerField);

			string Json = Encoding.UTF8.GetString(Data);
			if (JSON.Parse(Json) is Dictionary<string, object> Parsed)
			{
				foreach (KeyValuePair<string, object> P in Parsed)
					PrintField(P.Key, P.Value?.ToString(), RowPerField);
			}
		}

		private static void InteroperableDataReceived(byte[] Data, int? RedrawMenu, Dictionary<string, int> RowPerField)
		{
			SensorData SensorData = ParseSensorData(Data);
			if (SensorData is null)
				return;

			if (RedrawMenu.HasValue)
				ShowMenu(RedrawMenu.Value, RowPerField);

			foreach (Field Field in SensorData.Fields)
				PrintField(Field.Name, Field.ValueString, RowPerField);
		}

		private static SensorData ParseSensorData(byte[] Data)
		{
			if (Data.Length > 65536)
				return null;

			string Xml = Encoding.UTF8.GetString(Data);
			XmlDocument Doc = new();
			Doc.LoadXml(Xml);

			SensorData Result = SensorClient.ParseFields(Doc.DocumentElement);
			if (Result?.Fields is null)
				return null;

			return Result;
		}

		private static void InteroperableSignedPublicDataReceived(byte[] Data, EllipticCurve Cipher, byte[] RemotePublicKey,
			int? RedrawMenu, Dictionary<string, int> RowPerField)
		{
			SensorData SensorData = ParseSignedSensorData(Data, Cipher, RemotePublicKey);
			if (SensorData is null)
				return;

			if (RedrawMenu.HasValue)
				ShowMenu(RedrawMenu.Value, RowPerField);

			foreach (Field Field in SensorData.Fields)
				PrintField(Field.Name, Field.ValueString, RowPerField);
		}

		private static SensorData ParseSignedSensorData(byte[] Data, EllipticCurve Cipher, byte[] RemotePublicKey)
		{
			SensorData SensorData = ParseSensorData(Data);
			if (SensorData is null)
				return null;

			List<Field> BeforeSignature = [];
			string Signature = null;

			foreach (Field Field in SensorData.Fields)
			{
				if (Field.Name == "Signature")
				{
					if (Signature is null)
						Signature = Field.ValueString;
					else
						return null;
				}
				else
					BeforeSignature.Add(Field);
			}

			if (string.IsNullOrEmpty(Signature) || Signature.Length > 100)
				return null;

			try
			{
				byte[] SignatureBin = Base64Url.Decode(Signature);

				SensorData = new SensorData(BeforeSignature);

				string Xml = SensorData.PayloadXml;
				byte[] Bin = Encoding.UTF8.GetBytes(Xml);

				if (!Cipher.Verify(Bin, RemotePublicKey, SignatureBin))
					return null;
			}
			catch
			{
				return null;
			}

			return SensorData;
		}

		private static void InteroperableConfidentialDataReceived(byte[] Data, EllipticCurve Cipher, byte[] RemotePublicKey,
			int? RedrawMenu, Dictionary<string, int> RowPerField)
		{
			int c = Data.Length;
			if (c <= 32)
				return;

			byte[] Key = Cipher.GetSharedKey(RemotePublicKey, sha3.ComputeVariable);
			byte[] Nonce = new byte[16];
			byte[] IV = new byte[16];
			byte[] Encrypted = new byte[c - 32];

			Array.Copy(Data, 0, IV, 0, 16);
			Array.Copy(Data, 16, Nonce, 0, 16);
			Array.Copy(Data, 32, Encrypted, 0, c - 32);

			using Aes Aes = Aes.Create();
			Aes.BlockSize = 128;
			Aes.KeySize = 256;
			Aes.Mode = CipherMode.CBC;
			Aes.Padding = PaddingMode.PKCS7;

			byte[] Decrypted;

			try
			{
				using ICryptoTransform Decryptor = Aes.CreateDecryptor(Key, IV);
				Decrypted = Decryptor.TransformFinalBlock(Encrypted, 0, Encrypted.Length);
			}
			catch
			{
				return;
			}

			SensorData SensorData = ParseSignedSensorData(Decrypted, Cipher, RemotePublicKey);
			if (SensorData is null)
				return;

			if (RedrawMenu.HasValue)
				ShowMenu(RedrawMenu.Value, RowPerField);

			foreach (Field Field in SensorData.Fields)
				PrintField(Field.Name, Field.ValueString, RowPerField);
		}

		private static readonly Waher.Security.SHA3.SHA3_256 sha3 = new();

		#endregion
	}
}
