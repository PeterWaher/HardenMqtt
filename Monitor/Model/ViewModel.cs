using System;
using System.ComponentModel;
using System.Windows;
using Waher.Events;

namespace Monitor.Model
{
	/// <summary>
	/// Base class of view models.
	/// </summary>
	public class ViewModel : INotifyPropertyChanged
	{
		/// <summary>
		/// Base class of view models.
		/// </summary>
		public ViewModel()
		{
		}

		/// <summary>
		/// Event raised when a property in the view model has changed
		/// </summary>
		public event PropertyChangedEventHandler PropertyChanged;

		/// <summary>
		/// Raises the <see cref="PropertyChanged"/> event.
		/// </summary>
		/// <param name="PropertyName">Name of property that has changed.</param>
		public void OnPropertyChanged(string PropertyName)
		{
			Application.Current?.Dispatcher.Invoke(() =>
			{
				try
				{
					this.PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(PropertyName));
				}
				catch (Exception ex)
				{
					Log.Exception(ex);
				}
			});
		}
	}
}
