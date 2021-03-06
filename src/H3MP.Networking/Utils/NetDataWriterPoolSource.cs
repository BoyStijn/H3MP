using H3MP.Utils;
using LiteNetLib.Utils;

namespace H3MP.Networking
{
	public class NetDataWriterPoolSource : IPoolSource<NetDataWriter>
	{
		public NetDataWriter Create()
		{
			return new NetDataWriter();
		}

		public void Clean(NetDataWriter item)
		{
			item.Reset();
		}
	}
}
