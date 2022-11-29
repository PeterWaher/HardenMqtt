using System.Collections.Generic;
using System.Text;
using Waher.Content;
using Waher.Security.EllipticCurves;

namespace Pairing
{
	/// <summary>
	/// Contains current status of a pairing.
	/// </summary>
	public class PairingInformation
	{
		/// <summary>
		/// Nonce value.
		/// </summary>
		public string Nonce { get; set; }

		/// <summary>
		/// Public Key of master device
		/// </summary>
		public string MasterPublicKey { get; set; }

		/// <summary>
		/// Device ID of master device
		/// </summary>
		public string MasterId { get; set; }

		/// <summary>
		/// Device Type of master device
		/// </summary>
		public string MasterType { get; set; }

		/// <summary>
		/// Signature of information, by master device
		/// </summary>
		public string MasterSignature { get; set; }

		/// <summary>
		/// Public Key of slave device
		/// </summary>
		public string SlavePublicKey { get; set; }

		/// <summary>
		/// Device ID of slave device
		/// </summary>
		public string SlaveId { get; set; }

		/// <summary>
		/// Device Type of slave device
		/// </summary>
		public string SlaveType { get; set; }

		/// <summary>
		/// Signature of information, by slave device
		/// </summary>
		public string SlaveSignature { get; set; }

		/// <summary>
		/// If information has been completed.
		/// </summary>
		public bool Completed
		{
			get
			{
				return
					this.MasterCompleted &&
					this.SlaveCompleted;
			}
		}

		/// <summary>
		/// If master has completed its selection.
		/// </summary>
		public bool MasterCompleted
		{
			get
			{
				return
					!string.IsNullOrEmpty(this.MasterPublicKey) &&
					!string.IsNullOrEmpty(this.MasterId) &&
					!string.IsNullOrEmpty(this.MasterType) &&
					!string.IsNullOrEmpty(this.MasterSignature);
			}
		}

		/// <summary>
		/// If slave has completed its selection.
		/// </summary>
		public bool SlaveCompleted
		{
			get
			{
				return
					!string.IsNullOrEmpty(this.SlavePublicKey) &&
					!string.IsNullOrEmpty(this.SlaveId) &&
					!string.IsNullOrEmpty(this.SlaveType) &&
					!string.IsNullOrEmpty(this.SlaveSignature);
			}
		}

		/// <summary>
		/// Local Public Key
		/// </summary>
		/// <param name="LocalIsMaster">If the local device is master (true) or slave (false).</param>
		/// <returns>Local Public Key</returns>
		public string LocalPublicKey(bool LocalIsMaster) => LocalIsMaster ? this.MasterPublicKey : this.SlavePublicKey;

		/// <summary>
		/// Remote Public Key
		/// </summary>
		/// <param name="LocalIsMaster">If the local device is master (true) or slave (false).</param>
		/// <returns>Remote Public Key</returns>
		public string RemotePublicKey(bool LocalIsMaster) => LocalIsMaster ? this.SlavePublicKey : this.MasterPublicKey;

		/// <summary>
		/// Remote Device ID
		/// </summary>
		/// <param name="LocalIsMaster">If the local device is master (true) or slave (false).</param>
		/// <returns>Remote Device ID</returns>
		public string RemoteDeviceId(bool LocalIsMaster) => LocalIsMaster ? this.SlaveId : this.MasterId;

		/// <summary>
		/// Tries to parse information from a binary BLOB.
		/// </summary>
		/// <param name="Cipher">Cipher used by the local device.</param>
		/// <param name="Binary">Binary representatio of object.</param>
		/// <param name="ExpectedMasterType">Expected master type.</param>
		/// <param name="ExpectedSlaveType">Expected slave type.</param>
		/// <param name="Result">Parsed information, if successful.</param>
		/// <returns>If binary BLOB could be parsed</returns>
		public static bool TryParse(EllipticCurve Cipher, byte[] Binary, string ExpectedMasterType, string ExpectedSlaveType,
			out PairingInformation Result)
		{
			Result = null;

			if (Binary is null || Binary.Length > 1000)
				return false;

			try
			{
				string s = Encoding.UTF8.GetString(Binary);
				if (!(JSON.Parse(s) is Dictionary<string, object> Obj))
					return false;

				Obj.Remove("Completed");
				Obj.Remove("MasterCompleted");
				Obj.Remove("SlaveCompleted");

				PairingInformation Info = new PairingInformation();

				foreach (KeyValuePair<string, object> P in Obj)
				{
					if (!(P.Value is string Value))
					{
						if (P.Value is null)
							Value = null;	// Valid value.
						else
							return false;
					}

					switch (P.Key)
					{
						case nameof(Nonce):
							Info.Nonce = Value;
							break;

						case nameof(MasterPublicKey):
							Info.MasterPublicKey = Value;
							break;

						case nameof(MasterId):
							Info.MasterId = Value;
							break;

						case nameof(MasterType):
							Info.MasterType = Value;
							break;

						case nameof(MasterSignature):
							Info.MasterSignature = Value;
							break;

						case nameof(SlavePublicKey):
							Info.SlavePublicKey = Value;
							break;

						case nameof(SlaveId):
							Info.SlaveId = Value;
							break;

						case nameof(SlaveType):
							Info.SlaveType = Value;
							break;

						case nameof(SlaveSignature):
							Info.SlaveSignature = Value;
							break;

						default:
							return false;
					}
				}

				if (Info.MasterType != ExpectedMasterType || Info.SlaveType != ExpectedSlaveType)
					return false;

				byte[] SignedData = Info.GetSignData();

				if (!string.IsNullOrEmpty(Info.MasterPublicKey) &&
					!Cipher.Verify(SignedData, Base64Url.Decode(Info.MasterPublicKey), Base64Url.Decode(Info.MasterSignature)))
				{
					return false;
				}

				if (!string.IsNullOrEmpty(Info.SlaveSignature) &&
					!Cipher.Verify(SignedData, Base64Url.Decode(Info.SlavePublicKey), Base64Url.Decode(Info.SlaveSignature)))
				{
					return false;
				}

				Result = Info;
				return true;
			}
			catch
			{
				return false;
			}
		}

		/// <summary>
		/// Gets data used for signatures.
		/// </summary>
		/// <returns>Data for signatures.</returns>
		public byte[] GetSignData()
		{
			StringBuilder sb = new StringBuilder();

			sb.Append(this.Nonce);
			sb.Append('|');
			sb.Append(this.MasterPublicKey);
			sb.Append('|');
			sb.Append(this.MasterId);
			sb.Append('|');
			sb.Append(this.MasterType);
			sb.Append('|');
			sb.Append(this.SlavePublicKey);
			sb.Append('|');
			sb.Append(this.SlaveId);
			sb.Append('|');
			sb.Append(this.SlaveType);

			return Encoding.UTF8.GetBytes(sb.ToString());
		}

		/// <summary>
		/// Creates a signature for the information.
		/// </summary>
		/// <param name="Cipher">Cipher to use to sign the data.</param>
		/// <returns>Signature.</returns>
		public string Sign(EllipticCurve Cipher)
		{
			byte[] Data = this.GetSignData();
			byte[] Signature = Cipher.Sign(Data);
			return Base64Url.Encode(Signature);
		}

	}
}
