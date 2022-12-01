using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using Waher.Networking.MQTT;

namespace Monitor.Converters
{
	/// <summary>
	/// Converts an <see cref="MqttQualityOfService"/> to an integer.
	/// </summary>
	public class QosToInt : MarkupExtension, IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is MqttQualityOfService QoS)
			{
				switch (QoS)
				{
					case MqttQualityOfService.AtMostOnce: return 0;
					case MqttQualityOfService.AtLeastOnce: return 1;
					case MqttQualityOfService.ExactlyOnce: return 2;
				}
			}

			return 0;
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			if (value is int i)
			{
				switch (i)
				{
					case 0: return MqttQualityOfService.AtMostOnce;
					case 1:return MqttQualityOfService.AtLeastOnce;
					case 2:return MqttQualityOfService.ExactlyOnce;
				}
			}

			return value;
		}

		public override object ProvideValue(IServiceProvider serviceProvider)
		{
			return this;
		}
	}
}
