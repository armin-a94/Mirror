﻿using System;

namespace FossilDeltaX
{
	public class Delta
	{
		public static ushort HASHSIZE = 16;

		// insert: <<command, size, bytes>>
		static void WriteInsert(Writer writer, byte[] bytes, int offset, int count)
		{
			// varint for minium bandwidth
			writer.WriteByte(Command.INSERT);
			writer.WriteVarInt((uint)count);
			writer.WriteBytes(bytes, offset, count);
		}

		// copy: <<command, count, offset>>
		static void WriteCopy(Writer writer, int count, int offset)
		{
			// varint for minium bandwidth
			writer.WriteByte(Command.COPY);
			writer.WriteVarInt((uint)count);
			writer.WriteVarInt((uint)offset);
		}

		// Compute the hash table used to locate matching sections in the source.
		static void ComputeHashTable(byte[] A, RollingHash hash, out int nHash, out int[] collide, out int[] landmark)
		{
			nHash = A.Length / HASHSIZE;
			collide = new int[nHash];
			landmark = new int[nHash];

			for (int i = 0; i < collide.Length; i++) collide[i] = -1;
			for (int i = 0; i < landmark.Length; i++) landmark[i] = -1;

			for (int i = 0; i < A.Length-HASHSIZE; i += HASHSIZE)
			{
				hash.Init(A, i);
				int hv = (int) (hash.Value() % nHash);
				collide[i/HASHSIZE] = landmark[hv];
				landmark[hv] = i/HASHSIZE;
			}
		}

		// match result for the search algorithm
		struct Match
		{
			public int cnt;
			public int offset;
			public int litsz;

			public Match(int cnt, int offset, int litsz)
			{
				this.cnt = cnt;
				this.offset = offset;
				this.litsz = litsz;
			}
		}

		// magic search function
		static bool Search(byte[] A, byte[] B, RollingHash hash, int nHash, int[] collide, int[] landmark,
			int _base, int i,
			out Match match)
		{
			bool anyFound = false;
			match = new Match(0, 0, 0);

			int limit = 250;
			int hv = (int)(hash.Value() % nHash);
			int blockIndex = landmark[hv];
			while (blockIndex >= 0 && (limit--) > 0)
			{
				//
				// The hash window has identified a potential match against
				// landmark block iBlock. But we need to investigate further.
				//
				// Look for a region in zOut that matches zSrc. Anchor the search
				// at zSrc[iSrc] and zOut[_base+i]. Do not include anything prior to
				// zOut[_base] or after zOut[outLen] nor anything after zSrc[srcLen].
				//
				// Set cnt equal to the length of the match and set offset so that
				// zSrc[offset] is the first element of the match. litsz is the number
				// of characters between zOut[_base] and the beginning of the match.
				// sz will be the overhead (in bytes) needed to encode the copy
				// command. Only generate copy command if the overhead of the
				// copy command is less than the amount of literal text to be copied.
				//
				int j, k, x, y;

				// Beginning at iSrc, match forwards as far as we can.
				// j counts the number of characters that match.
				int iSrc = blockIndex * HASHSIZE;
				for (j = 0, x = iSrc, y = _base+i; x < A.Length && y < B.Length; j++, x++, y++)
				{
					if (A[x] != B[y]) break;
				}
				j--;

				// Beginning at iSrc-1, match backwards as far as we can.
				// k counts the number of characters that match.
				for (k = 1; k < iSrc && k <= i; k++)
				{
					if (A[iSrc-k] != B[_base+i-k]) break;
				}
				k--;

				// Compute the offset and size of the matching region.
				int offset = iSrc-k;
				int cnt = j+k+1;
				int litsz = i-k;
				// sz will hold the number of bytes needed to encode the "insert"
				// command and the copy command, not counting the "insert" text.
				// -> we use varint, so we need to calculate byte sizes here
				// TODO make sure i-k is always >0 otherwise varint is too big
				int sz = VarIntSize((ulong)(i-k)) + VarIntSize((ulong)cnt) + VarIntSize((ulong)offset) + 3;
				if (cnt >= sz && cnt > match.cnt)
				{
					// Remember this match only if it is the best so far and it
					// does not increase the file size.
					match = new Match(cnt, iSrc-k, litsz);
					anyFound = true;
				}

				// Check the next matching block
				blockIndex = collide[blockIndex];
			}

			return anyFound;
		}

		public static byte[] Create(byte[] A, byte[] B)
		{
			Writer writer = new Writer();
			int lastRead = -1;

			// If the source is very small, it means that we have no
			// chance of ever doing a copy command. Just output a single
			// literal segment for the entire target and exit.
			if (A.Length <= HASHSIZE)
			{
				WriteInsert(writer, B, 0, B.Length);
				return writer.ToArray();
			}

			// Compute the hash table used to locate matching sections in the source.
			RollingHash hash = new RollingHash();
			ComputeHashTable(A, hash, out int nHash, out int[] collide, out int[] landmark);

			// _base seems to be the offset of current chunk
			int _base = 0;
			while (_base + HASHSIZE < B.Length)
			{
				hash.Init(B, _base);
				int i = 0;
				while (true)
				{
					// search best match (if any)
					bool found = Search(A, B, hash, nHash, collide, landmark, _base, i, out Match match);

					// We have a copy command that does not cause the delta to be larger
					// than a literal insert. So add the copy command to the delta.
					if (found)
					{
						if (match.litsz > 0)
						{
							// Add an insert command before the copy.
							WriteInsert(writer, B, _base, match.litsz);
							_base += match.litsz;
						}
						_base += match.cnt;

						// COPY command
						WriteCopy(writer, match.cnt, match.offset);
						if (match.offset + match.cnt -1 > lastRead)
						{
							lastRead = match.offset + match.cnt - 1;
						}
						break;
					}

					// If we reach this point, it means no match is found so far

					// reached the end? and not found any matches?
					if (_base + i + HASHSIZE >= B.Length)
					{
						// do an "insert" for everything that does not match
						WriteInsert(writer, B, _base, B.Length - _base);
						_base = B.Length;
						break;
					}

					// no match found, but not at the end yet.
					// Advance the hash by one character. Keep looking for a match.
					hash.Next(B[_base + i + HASHSIZE]);
					i++;
				}
			}

			// Output a final "insert" record to get all the text at the end of
			// the file that does not match anything in the source.
			if (_base < B.Length)
			{
				WriteInsert(writer, B, _base, B.Length - _base);
			}

			return writer.ToArray();
		}

		static void ProcessCopyCommand(byte[] A, Reader reader, Writer writer, ref uint total)
		{
			uint count = (uint)reader.ReadVarInt();
			uint offset = (uint)reader.ReadVarInt();

			total += count;
			// 'limit' header was removed to reduce bandwidth
			//if (total > limit)
			//	throw new Exception("copy exceeds output file size");
			if (offset + count > A.Length)
				throw new Exception("copy extends past end of input");

			writer.WriteBytes(A, (int)offset, (int)count);
		}

		static void ProcessInsertCommand(byte[] delta, Reader reader, Writer writer, ref uint total)
		{
			uint count = (uint)reader.ReadVarInt();

			total += count;

			// 'limit' header was removed to reduce bandwidth
			//if (total > limit)
			//	throw new Exception("insert command gives an output larger than predicted");
			if (count > delta.Length)
				throw new Exception("insert count exceeds size of delta");

			writer.WriteBytes(reader.a, (int)reader.pos, (int)count);
			reader.pos += count;
		}

		public static byte[] Apply(byte[] A, byte[] delta)
		{
			uint total = 0;
			Reader deltaReader = new Reader(delta);

			Writer writer = new Writer();
			while(deltaReader.HaveBytes())
			{
				int command = deltaReader.ReadByte();
				switch (command)
				{
					// copy command
					case Command.COPY:
					{
						ProcessCopyCommand(A, deltaReader, writer, ref total);
						break;
					}

					// insert command
					case Command.INSERT:
					{
						ProcessInsertCommand(delta, deltaReader, writer, ref total);
						break;
					}

					default:
						throw new Exception($"unknown delta operator: 0x{command:X2}");
				}
			}

			// done parsing
			return writer.ToArray();
		}

		// predict how many bytes we need for varint for a value
		internal static int VarIntSize(ulong value)
		{
            if (value <= 240)
            {
                return 1;
            }
            if (value <= 2287)
            {
                return 2;
            }
            if (value <= 67823)
            {
                return 3;
            }
            if (value <= 16777215)
            {
                return 4;
            }
            if (value <= 4294967295)
            {
                return 5;
            }
            if (value <= 1099511627775)
            {
                return 6;
            }
            if (value <= 281474976710655)
            {
                return 7;
            }
            if (value <= 72057594037927935)
            {
                return 8;
            }

            // all others
            return 9;
		}
	}
}