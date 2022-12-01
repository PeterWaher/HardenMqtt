namespace Monitor.Model
{
	/// <summary>
	/// Represents a bindable property in a view model.
	/// </summary>
	/// <typeparam name="T">Property type value.</typeparam>
	public class BindableProperty<T>
	{
		private readonly ViewModel model;
		private readonly string name;
		private T value;

		/// <summary>
		/// Represents a bindable property in a view model.
		/// </summary>
		/// <param name="Model">View Model to which the bindable property belongs.</param>
		/// <param name="Name">Name of property</param>
		public BindableProperty(ViewModel Model, string Name)
			: this(Model, Name, default)
		{
		}

		/// <summary>
		/// Represents a bindable property in a view model.
		/// </summary>
		/// <param name="Model">View Model to which the bindable property belongs.</param>
		/// <param name="Name">Name of property</param>
		/// <param name="Value">Value of property</param>
		public BindableProperty(ViewModel Model, string Name, T Value)
		{
			this.model = Model;
			this.name = Name;
			this.value = Value;
		}

		/// <summary>
		/// Name of property.
		/// </summary>
		public string Name => this.name;

		/// <summary>
		/// Property value.
		/// </summary>
		public T Value
		{
			get => this.value;
			set
			{
				this.value = value;
				this.model.OnPropertyChanged(this.name);
			}
		}
	}
}
