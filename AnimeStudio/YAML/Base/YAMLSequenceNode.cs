using System.Collections.Generic;

namespace AnimeStudio
{
	public sealed class YAMLSequenceNode : YAMLNode
	{
		public YAMLSequenceNode()
		{
		}

		public YAMLSequenceNode(SequenceStyle style)
		{
			Style = style;
		}

		/// <summary>
		/// For sequences whose length is known: the child list otherwise grows by doubling,
		/// and animation YAML builds one sequence per curve with dozens to thousands of
		/// keyframes. Growing those lists was the single largest item in an export profile.
		/// </summary>
		public YAMLSequenceNode(SequenceStyle style, int capacity)
		{
			Style = style;
			if (capacity > 0)
			{
				m_children.Capacity = capacity;
			}
		}

		public void Add(bool value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(byte value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(short value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(ushort value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(int value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(uint value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(long value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(ulong value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(float value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(double value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value, Style.IsRaw());
			Add(node);
		}

		public void Add(string value)
		{
			YAMLScalarNode node = new YAMLScalarNode(value);
			Add(node);
		}

		public void Add(YAMLNode child)
		{
			m_children.Add(child);
		}

		internal override void Emit(Emitter emitter)
		{
			base.Emit(emitter);

			StartChildren(emitter);
			foreach (YAMLNode child in m_children)
			{
				StartChild(emitter, child);
				child.Emit(emitter);
				EndChild(emitter, child);
			}
			EndChildren(emitter);
		}

		private void StartChildren(Emitter emitter)
		{
			switch (Style)
			{
				case SequenceStyle.Block:
					if (m_children.Count == 0)
					{
						emitter.Write('[');
					}
					break;

				case SequenceStyle.BlockCurve:
					if (m_children.Count == 0)
					{
						emitter.Write('{');
					}
					break;

				case SequenceStyle.Flow:
					emitter.Write('[');
					break;

				case SequenceStyle.Raw:
					if (m_children.Count == 0)
					{
						emitter.Write('[');
					}
					break;
			}
		}

		private void EndChildren(Emitter emitter)
		{
			switch (Style)
			{
				case SequenceStyle.Block:
					if (m_children.Count == 0)
					{
						emitter.Write(']');
					}
					emitter.WriteLine();
					break;

				case SequenceStyle.BlockCurve:
					if (m_children.Count == 0)
					{
						emitter.WriteClose('}');
					}
					emitter.WriteLine();
					break;

				case SequenceStyle.Flow:
					emitter.WriteClose(']');
					break;

				case SequenceStyle.Raw:
					if (m_children.Count == 0)
					{
						emitter.Write(']');
					}
					emitter.WriteLine();
					break;
			}
		}

		private void StartChild(Emitter emitter, YAMLNode next)
		{
			if (Style.IsAnyBlock())
			{
				emitter.Write('-').Write(' ');

				if (next.NodeType == NodeType)
				{
					emitter.IncreaseIndent();
				}
			}
			if (next.IsIndent)
			{
				emitter.IncreaseIndent();
			}
		}

		private void EndChild(Emitter emitter, YAMLNode next)
		{
			if (Style.IsAnyBlock())
			{
				emitter.WriteLine();
				if (next.NodeType == NodeType)
				{
					emitter.DecreaseIndent();
				}
			}
			else if (Style == SequenceStyle.Flow)
			{
				emitter.WriteSeparator().WriteWhitespace();
			}
			if (next.IsIndent)
			{
				emitter.DecreaseIndent();
			}
		}

		public static YAMLSequenceNode Empty { get; } = new YAMLSequenceNode();

		public override YAMLNodeType NodeType => YAMLNodeType.Sequence;
		public override bool IsMultiline => Style.IsAnyBlock() && m_children.Count > 0;
		public override bool IsIndent => false;

		public SequenceStyle Style { get; }

		private readonly List<YAMLNode> m_children = new List<YAMLNode>();
	}
}
