// <auto-generated/>
using System;
using Telegram.Api.Native.TL;

namespace Telegram.Api.TL
{
	public partial class TLUpdateRecentStickers : TLUpdateBase 
	{
		public TLUpdateRecentStickers() { }
		public TLUpdateRecentStickers(TLBinaryReader from)
		{
			Read(from);
		}

		public override TLType TypeId { get { return TLType.UpdateRecentStickers; } }

		public override void Read(TLBinaryReader from)
		{
		}

		public override void Write(TLBinaryWriter to)
		{
			to.WriteUInt32(0x9A422C20);
		}
	}
}