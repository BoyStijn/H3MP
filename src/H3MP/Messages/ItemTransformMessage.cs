using FistVR;
using H3MP.Utils;
using LiteNetLib.Utils;
using UnityEngine;

namespace H3MP.Messages
{
	public struct ItemTransformsMessage : INetSerializable, ILinearFittable<ItemTransformsMessage>
	{
		public TransformMessage Item { get; private set; }

		public ItemTransformsMessage(TransformMessage item)
		{
			Item = item;
		}

		public ItemTransformsMessage(Transform item) : this(new TransformMessage(item))
		{
		}

		public ItemTransformsMessage Fit(ItemTransformsMessage other, double t)
		{
			return new ItemTransformsMessage(
				Item.Fit(other.Item, t)
			);
		}

		public void Deserialize(NetDataReader reader)
		{
			Item = reader.Get<TransformMessage>();
		}

		public void Serialize(NetDataWriter writer)
		{
			writer.Put(Item);
		}
	}
}
