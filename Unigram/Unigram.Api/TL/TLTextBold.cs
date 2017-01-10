// <auto-generated/>
using System;

namespace Telegram.Api.TL
{
	public partial class TLTextBold : TLRichTextBase 
	{
		public TLRichTextBase Text { get; set; }

		public TLTextBold() { }
		public TLTextBold(TLBinaryReader from, bool cache = false)
		{
			Read(from, cache);
		}

		public override TLType TypeId { get { return TLType.TextBold; } }

		public override void Read(TLBinaryReader from, bool cache = false)
		{
			Text = TLFactory.Read<TLRichTextBase>(from, cache);
			if (cache) ReadFromCache(from);
		}

		public override void Write(TLBinaryWriter to, bool cache = false)
		{
			to.Write(0x6724ABC4);
			to.WriteObject(Text, cache);
			if (cache) WriteToCache(to);
		}
	}
}