using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace Monitor.Model
{
	/// <summary>
	/// Model of a topic
	/// </summary>
	public class MqttTopic : ViewModel, IComparer<MqttTopic>
	{
		private readonly ObservableCollection<MqttTopic> items = new ObservableCollection<MqttTopic>();

		/// <summary>
		/// Model of a topic
		/// </summary>
		/// <param name="Topic">Topic string</param>
		public MqttTopic(string Topic)
		{
			this.Topic = Topic;

			int i = Topic.LastIndexOf('/');
			if (i < 0)
				this.LocalName = Topic;
			else
				this.LocalName = Topic[(i + 1)..];
		}

		/// <summary>
		/// Local Name
		/// </summary>
		public string LocalName { get; }

		/// <summary>
		/// Topic string
		/// </summary>
		public string Topic { get; }

		/// <summary>
		/// Child topics
		/// </summary>
		public ObservableCollection<MqttTopic> Items => this.items;

		/// <summary>
		/// Orders topics by local name.
		/// </summary>
		/// <param name="x">Topic 1</param>
		/// <param name="y">Topic 2</param>
		/// <returns>Local name order.</returns>
		public int Compare(MqttTopic x, MqttTopic y)
		{
			return x.LocalName.CompareTo(y.LocalName);
		}
	}
}
