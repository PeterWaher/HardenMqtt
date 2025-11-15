using System;
using System.Globalization;
using System.Text;
using System.Windows.Data;
using System.Windows.Markup;
using System.Xml;
using Waher.Content;
using Waher.Content.Xml;
using Waher.Networking.MQTT;

namespace Monitor.Converters
{
	/// <summary>
	/// Converts an <see cref="MqttQualityOfService"/> to an integer.
	/// </summary>
	public class BinaryToText : MarkupExtension, IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is byte[] Binary)
			{
				if (Binary.Length > 4096)
					return "<" + Binary.Length.ToString() + " bytes>";
				else
				{
					string s = Encoding.UTF8.GetString(Binary);

					if ((s.StartsWith('{') && s.EndsWith('}')) ||
						(s.StartsWith('[') && s.EndsWith(']')))
					{
						try
						{
							object Obj = JSON.Parse(s);
							s = JSON.Encode(Obj, true);
						}
						catch
						{
							// Ignore
						}
					}
					else if (s.StartsWith('<') && s.EndsWith('>') && XML.IsValidXml(s))
					{
						try
						{
							XmlDocument Xml = new();
							Xml.LoadXml(s);

							XmlWriterSettings Settings = XML.WriterSettings(true, true);
							StringBuilder sb = new();
							using XmlWriter Output = XmlWriter.Create(sb, Settings);
							Xml.Save(Output);
							Output.Flush();

							s = sb.ToString();
						}
						catch
						{
							// Ignore
						}
					}

					return s;
				}
			}

			return value;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is string s)
				return Encoding.UTF8.GetBytes(s);

			return value;
		}

		public override object ProvideValue(IServiceProvider serviceProvider)
		{
			return this;
		}
	}
}
