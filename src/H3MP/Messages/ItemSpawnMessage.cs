using LiteNetLib.Utils;

namespace H3MP.Messages
{
	public struct ItemSpawnMessage : INetSerializable
	{
		public byte ID { get; private set; }

		public Timestamped<ItemTransformsMessage> Transforms { get; private set; }

		public ItemSpawnMessage(byte id, Timestamped<ItemTransformsMessage> transforms)
		{
			ID = id;
			Transforms = transforms;
		}

		public void Deserialize(NetDataReader reader)
		{
			ID = reader.GetByte();
			Transforms = reader.Get<Timestamped<ItemTransformsMessage>>();
		}

		public void Serialize(NetDataWriter writer)
		{
			writer.Put(ID);
			writer.Put(Transforms);
		}
	}
}

