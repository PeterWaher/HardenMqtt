using System.Collections.Generic;
using System.Text;
using System;
using System.Threading.Tasks;
using Waher.Content;
using Waher.Events;
using Waher.Networking.MQTT;
using Waher.Security;
using Waher.Security.EllipticCurves;
using System.Threading;

namespace Pairing
{
	/// <summary>
	/// Static class that helps creating device pairs securely over MQTT.
	/// </summary>
	public static class DevicePairing
	{
		/// <summary>
		/// Static methods that pairs the local device with a remote device, over MQTT. The remote device needs to execute
		/// the same method over the same MQTT broker, for this pairing mechanism to work. One of the devices is considered
		/// the master, while the other is the slave. The master initiates and controls the pairing, while the slave 
		/// responds to the masters messages.
		/// </summary>
		/// <param name="Mqtt">MQTT Client</param>
		/// <param name="LocalCipher">Cipher used by the local device to cryptographically sign information.</param>
		/// <param name="LocalDeviceID">ID of local device.</param>
		/// <param name="LocalDeviceType">Type of local device.</param>
		/// <param name="RemoteDeviceType">Type of remote device expected.</param>
		/// <param name="Nonce">Random bytes, to make each pairing session unique, cryptographically.</param>
		/// <param name="LocalIsMaster">If the local device is the master of the pairing process.</param>
		/// <param name="Cancel">Cancellation token that can be used to abort the pairing process.</param>
		/// <returns>Completed pairing information. The same information is returned to the remote device.
		/// If operation is cancelled, null is returned.</returns>
		public static async Task<PairingInformation> PairDevice(MqttClient Mqtt, EllipticCurve LocalCipher,
			string LocalDeviceID, string LocalDeviceType, string RemoteDeviceType, byte[] Nonce, bool LocalIsMaster,
			CancellationToken Cancel)
		{
			PairingInformation Pairing = new PairingInformation()
			{
				Nonce = Base64Url.Encode(Nonce)
			};
			string LocalPublicKey = Base64Url.Encode(LocalCipher.PublicKey);
			string ExpectedMasterType;
			string ExpectedSlaveType;

			if (LocalIsMaster)
			{
				Pairing.MasterPublicKey = LocalPublicKey;
				Pairing.MasterId = LocalDeviceID;
				Pairing.MasterType = ExpectedMasterType = LocalDeviceType;
				Pairing.SlaveType = ExpectedSlaveType = RemoteDeviceType;
				Pairing.MasterSignature = Pairing.Sign(LocalCipher);
			}
			else
			{
				Pairing.SlavePublicKey = LocalPublicKey;
				Pairing.SlaveId = LocalDeviceID;
				Pairing.SlaveType = ExpectedSlaveType = LocalDeviceType;
				Pairing.MasterType = ExpectedMasterType = RemoteDeviceType;
				Pairing.SlaveSignature = Pairing.Sign(LocalCipher);
			}

			// Publish pairing information every 5s while pairing.

			using Timer PairingTimer = new Timer(async (_) =>
				await PublishPairingStatus(Mqtt, Pairing), null, 1000, 5000);

			// Receiving public key of device ready to be paired.

			Dictionary<int, string> NrToKey = new Dictionary<int, string>();
			Dictionary<string, string> KeyToDeviceId = new Dictionary<string, string>();
			TaskCompletionSource<bool> Completed = new TaskCompletionSource<bool>();

			async Task CheckPairing(object sender, MqttContent e)
			{
				if (e.Topic == "HardenMqtt/Pairing" &&
					PairingInformation.TryParse(LocalCipher, e.Data, ExpectedMasterType, ExpectedSlaveType, out PairingInformation Info))
				{
					if (Info.Completed)
					{
						if (Info.LocalPublicKey(LocalIsMaster) != LocalPublicKey)
							return;  // Relates to other devices.

						Pairing = Info;
						Completed.TrySetResult(true);
					}
					else if (LocalIsMaster)
					{
						if (!string.IsNullOrEmpty(Info.MasterPublicKey))
							return;  // Relates to device that is already paired or in a pairing session.

						// Display available public keys the user can select from.

						lock (NrToKey)
						{
							string RemoteKey = Info.SlavePublicKey;
							string RemoteId = Info.SlaveId;

							if (!string.IsNullOrEmpty(RemoteKey) &&
								!string.IsNullOrEmpty(RemoteId) &&
								RemoteKey.Length < 100 &&
								RemoteId.Length < 100 &&
								!KeyToDeviceId.ContainsKey(RemoteKey))
							{
								try
								{
									byte[] RemoteKeyBin = Base64Url.Decode(RemoteKey);
									LocalCipher.GetSharedKey(RemoteKeyBin, Hashes.ComputeSHA256Hash);
								}
								catch
								{
									return;  // Invalid key
								}

								int KeyNr = NrToKey.Count + 1;
								NrToKey[KeyNr] = RemoteKey;
								KeyToDeviceId[RemoteKey] = RemoteId;

								StringBuilder sb = new StringBuilder();

								sb.Append("Device ready to be paired: ");
								sb.Append(KeyNr);
								sb.Append(". ");
								sb.Append(RemoteDeviceType);
								sb.Append(", ");
								sb.Append(RemoteId);
								sb.Append(": ");
								sb.Append(RemoteKey);

								Log.Notice(sb.ToString(), LocalDeviceID);
							}
						}
					}
					else if (Info.MasterCompleted && 
						!Info.SlaveCompleted && 
						Info.SlavePublicKey == LocalPublicKey &&
						Info.SlaveId == LocalDeviceID)
					{
						Info.SlaveSignature = Info.Sign(LocalCipher);	// Acknowledging receipt of selection by master.

						await PublishPairingStatus(Mqtt, Info);

						Log.Informational("Pairing to " + Info.MasterPublicKey + " (" + Info.MasterId + ")", LocalDeviceID);
					}
				}
			};

			Mqtt.OnContentReceived += CheckPairing;

			try
			{
				// Subscribe to pairing messages (sensor publishes pairing messages as part of sensor data publication)

				await Mqtt.SUBSCRIBE("HardenMqtt/Pairing");

				// Pair device

				if (LocalIsMaster)
				{
					while (!Pairing.Completed)
					{
						await Task.Delay(100);  // Permits CTRL+Z to cancel token properly. Without delay, you might have to press CTRL+Z two times, as cancellation token cannot be passed on to ReadLine method.

						if (Cancel.IsCancellationRequested)
							return null;

						if (string.IsNullOrEmpty(Pairing.SlavePublicKey))
						{
							Console.Out.WriteLine("Please choose the remote key to pair to below. You select the key by entering the number corresponding to the key, as presented in the event log as they are being received.");
							Console.Out.Write("Public Key of remote device (or index): ");

							string s = Console.In.ReadLine();
							if (string.IsNullOrEmpty(s))
								continue;

							if (!int.TryParse(s, out int Nr))
							{
								Console.Out.WriteLine("Not an integer number.");
								continue;
							}

							string SelectedId;
							string SelectedKey;
							byte[] SelectedKeyBin;

							lock (NrToKey)
							{
								if (!NrToKey.TryGetValue(Nr, out SelectedKey))
								{
									Console.Out.WriteLine("There is no key reported related to that number.");
									continue;
								}

								if (!KeyToDeviceId.TryGetValue(SelectedKey, out SelectedId))
								{
									Console.Out.WriteLine("There is no ID reported related to that key.");
									continue;
								}
							}

							try
							{
								// Check key is a valid public key

								byte[] Bin = Base64Url.Decode(SelectedKey);
								Edwards25519 Temp = new Edwards25519(Bin);

								SelectedKeyBin = Bin;
							}
							catch (Exception)
							{
								Log.Error("Invalid public key provided during pairing.", LocalDeviceID);
								continue;
							}

							Pairing.SlavePublicKey = SelectedKey;
							Pairing.SlaveId = SelectedId;
							Pairing.MasterSignature = Pairing.Sign(LocalCipher);
							Pairing.SlaveSignature = null;

							await PublishPairingStatus(Mqtt, Pairing);

							Log.Informational("Pairing to " + SelectedKey + " (" + SelectedId + ")", LocalDeviceID);
						}
					}
				}
				else
				{
					Cancel.Register(() => Completed.TrySetResult(false));

					if (!await Completed.Task)
						return null;
				}

				Log.Notice("Successfully paired to " + Pairing.RemotePublicKey(LocalIsMaster) + " (" + 
					Pairing.RemoteDeviceId(LocalIsMaster) + ")", LocalDeviceID);
			}
			finally
			{
				// Unsubscribe from pairing messages

				Mqtt.OnContentReceived -= CheckPairing;
				await Mqtt.UNSUBSCRIBE("HardenMqtt/Pairing");
			}

			return Pairing;
		}

		/// <summary>
		/// Publishes Pairing information on an MQTT Broker.
		/// </summary>
		/// <param name="Mqtt">MQTT client connection</param>
		/// <param name="Pairing">Pairing inforation to publish.</param>
		private static async Task PublishPairingStatus(MqttClient Mqtt, PairingInformation Pairing)
		{
			try
			{
				string Msg = JSON.Encode(Pairing, false);
				byte[] Binary = Encoding.UTF8.GetBytes(Msg);
				await Mqtt.PUBLISH("HardenMqtt/Pairing", MqttQualityOfService.AtMostOnce, false, Binary);   // Information is not retained on the topic
			}
			catch (Exception ex)
			{
				Log.Critical(ex);
			}
		}

	}
}
