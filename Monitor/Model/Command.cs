using System;
using System.Windows;
using System.Windows.Input;
using Waher.Events;

namespace Monitor.Model
{
	/// <summary>
	/// Delegate for CanExecute method calls.
	/// </summary>
	/// <returns>If command can be executed.</returns>
	public delegate bool CanExecuteDelegate();

	/// <summary>
	/// Delegate for Execute method calls.
	/// </summary>
	public delegate void ExecuteDelegate();

	/// <summary>
	/// Delegate for CanExecute method calls.
	/// </summary>
	/// <param name="Paramter">Parameter</param>
	/// <returns>If command can be executed.</returns>
	public delegate bool CanExecuteParameterDelegate(object Paramter);

	/// <summary>
	/// Delegate for Execute method calls.
	/// </summary>
	/// <param name="Paramter">Parameter</param>
	public delegate void ExecuteParameterDelegate(object Paramter);

	/// <summary>
	/// Custom bindable command.
	/// </summary>
	/// <param name="CanExecute">Method to call to evaluate if command can be executed.</param>
	/// <param name="Execute">Method to call when command is executed.</param>
	public class Command(CanExecuteParameterDelegate CanExecute, ExecuteParameterDelegate Execute) : ICommand
	{
		private readonly CanExecuteParameterDelegate canExecute = CanExecute;
		private readonly ExecuteParameterDelegate execute = Execute;

		/// <summary>
		/// Custom bindable command.
		/// </summary>
		/// <param name="Execute">Method to call when command is executed.</param>
		public Command(ExecuteDelegate Execute)
			: this(null, (_) => Execute())
		{
		}

		/// <summary>
		/// Custom bindable command.
		/// </summary>
		/// <param name="CanExecute">Method to call to evaluate if command can be executed.</param>
		/// <param name="Execute">Method to call when command is executed.</param>
		public Command(CanExecuteDelegate CanExecute, ExecuteDelegate Execute)
			: this((_) => CanExecute(), (_) => Execute())
		{
		}

		/// <summary>
		/// Custom bindable command.
		/// </summary>
		/// <param name="Execute">Method to call when command is executed.</param>
		public Command(ExecuteParameterDelegate Execute)
			: this(null, Execute)
		{
		}

		/// <summary>
		/// Call this method to refresh the command UI.
		/// </summary>
		public void Changed()
		{
			Application.Current?.Dispatcher.Invoke(() =>
			{
				this.CanExecuteChanged.Raise(this, EventArgs.Empty);
			});
		}

		/// <summary>
		/// Event raised when execution status has changed.
		/// </summary>
		public event EventHandler CanExecuteChanged;

		/// <summary>
		/// Evaluates if command can be executed.
		/// </summary>
		/// <param name="parameter">Parameter</param>
		/// <returns>If command can be executed.</returns>
		public bool CanExecute(object parameter)
		{
			try
			{
				return this.canExecute?.Invoke(parameter) ?? true;
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
				return false;
			}
		}

		/// <summary>
		/// Executes the command.
		/// </summary>
		/// <param name="parameter">Parameter</param>
		public void Execute(object parameter)
		{
			try
			{
				this.execute?.Invoke(parameter);
			}
			catch (Exception ex)
			{
				Log.Exception(ex);
			}
		}
	}
}
