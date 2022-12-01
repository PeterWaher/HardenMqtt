using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

namespace Monitor.Converters
{
	/// <summary>
	/// Performs a logical not operation on a boolean value.
	/// </summary>
	public class LogicalNot : MarkupExtension, IValueConverter
	{
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return !((bool)value);
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
		{
			return value;
		}

		public override object ProvideValue(IServiceProvider serviceProvider)
		{
			return this;
		}
	}
}
