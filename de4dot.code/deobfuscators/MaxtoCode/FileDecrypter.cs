﻿/*
    Copyright (C) 2011-2012 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.IO;
using System.Collections.Generic;
using Mono.MyStuff;
using de4dot.code.PE;

namespace de4dot.code.deobfuscators.MaxtoCode {
	class FileDecrypter {
		MainType mainType;

		class PeHeader {
			const int XOR_KEY = 0x7ABF931;
			const int RVA_DISPL_OFFSET = 0x0FB4;
			const int MC_HEADER_RVA_OFFSET = 0x0FFC;

			byte[] headerData;
			uint rvaDispl;

			public PeHeader(PeImage peImage) {
				headerData = getPeHeaderData(peImage);

				if (peImage.readUInt32(0x2008) != 0x48)
					rvaDispl = readUInt32(RVA_DISPL_OFFSET) ^ XOR_KEY;
			}

			public bool hasMagic(int offset, uint magic1, uint magic2) {
				return readUInt32(offset) == magic1 && readUInt32(offset + 4) == magic2;
			}

			public uint getMcHeaderRva() {
				return getRva(MC_HEADER_RVA_OFFSET, XOR_KEY);
			}

			public uint getRva(int offset, uint xorKey) {
				return (readUInt32(offset) ^ xorKey) - rvaDispl;
			}

			uint readUInt32(int offset) {
				return BitConverter.ToUInt32(headerData, offset);
			}

			static byte[] getPeHeaderData(PeImage peImage) {
				var data = new byte[0x1000];

				var firstSection = peImage.Sections[0];
				readTo(peImage, data, 0, 0, firstSection.pointerToRawData);

				foreach (var section in peImage.Sections) {
					if (section.virtualAddress >= data.Length)
						continue;
					int offset = (int)section.virtualAddress;
					readTo(peImage, data, offset, section.pointerToRawData, section.sizeOfRawData);
				}

				return data;
			}

			static void readTo(PeImage peImage, byte[] data, int destOffset, uint imageOffset, uint maxLength) {
				if (destOffset > data.Length)
					return;
				int len = Math.Min(data.Length - destOffset, (int)maxLength);
				var newData = peImage.offsetReadBytes(imageOffset, len);
				Array.Copy(newData, 0, data, destOffset, newData.Length);
			}
		}

		class McHeader {
			PeHeader peHeader;
			byte[] data;

			public McHeader(PeImage peImage, PeHeader peHeader) {
				this.peHeader = peHeader;
				this.data = peImage.readBytes(peHeader.getMcHeaderRva(), 0x2000);
			}

			public bool hasMagic(int offset, uint magic1, uint magic2) {
				return readUInt32(offset) == magic1 && readUInt32(offset + 4) == magic2;
			}

			public byte readByte(int offset) {
				return data[offset];
			}

			public void readBytes(int offset, Array dest, int size) {
				Buffer.BlockCopy(data, offset, dest, 0, size);
			}

			public uint readUInt32(int offset) {
				return BitConverter.ToUInt32(data, offset);
			}
		}

		class DecryptedMethodInfo {
			public uint bodyRva;
			public byte[] body;

			public DecryptedMethodInfo(uint bodyRva, byte[] body) {
				this.bodyRva = bodyRva;
				this.body = body;
			}
		}

		class MethodInfos {
			PeImage peImage;
			PeHeader peHeader;
			McHeader mcHeader;
			uint structSize;
			uint methodInfosOffset;
			uint encryptedDataOffset;
			uint xorKey;
			Dictionary<uint, DecryptedMethodInfo> infos = new Dictionary<uint, DecryptedMethodInfo>();
			const int ENCRYPTED_DATA_INFO_SIZE = 0x13;

			public MethodInfos(PeImage peImage, PeHeader peHeader, McHeader mcHeader) {
				this.peImage = peImage;
				this.peHeader = peHeader;
				this.mcHeader = mcHeader;

				if (mcHeader.hasMagic(0x08C0, 0x6A731B13, 0xD72B891F))
					structSize = 0xC + 6 * ENCRYPTED_DATA_INFO_SIZE;
				else
					structSize = 0xC + 3 * ENCRYPTED_DATA_INFO_SIZE;

				uint methodInfosRva = peHeader.getRva(0x0FF8, mcHeader.readUInt32(0x005A));
				uint encryptedDataRva = peHeader.getRva(0x0FF0, mcHeader.readUInt32(0x0046));

				methodInfosOffset = peImage.rvaToOffset(methodInfosRva);
				encryptedDataOffset = peImage.rvaToOffset(encryptedDataRva);
			}

			public DecryptedMethodInfo lookup(uint bodyRva) {
				DecryptedMethodInfo info;
				infos.TryGetValue(bodyRva, out info);
				return info;
			}

			byte readByte(uint offset) {
				return peImage.offsetReadByte(methodInfosOffset + offset);
			}

			short readInt16(uint offset) {
				return (short)peImage.offsetReadUInt16(methodInfosOffset + offset);
			}

			uint readUInt32(uint offset) {
				return peImage.offsetReadUInt32(methodInfosOffset + offset);
			}

			int readInt32(uint offset) {
				return (int)readUInt32(offset);
			}

			short readEncryptedInt16(uint offset) {
				return (short)(readInt16(offset) ^ xorKey);
			}

			int readEncryptedInt32(uint offset) {
				return (int)readEncryptedUInt32(offset);
			}

			uint readEncryptedUInt32(uint offset) {
				return readUInt32(offset) ^ xorKey;
			}

			public void initializeInfos() {
				int numMethods = readInt32(0) ^ readInt32(4);
				if (numMethods < 0)
					throw new ApplicationException("Invalid number of encrypted methods");

				xorKey = (uint)numMethods;
				uint rvaDispl = peImage.readUInt32(0x2008) != 0x48 ? 0x1000U : 0;
				int numEncryptedDataInfos = ((int)structSize - 0xC) / ENCRYPTED_DATA_INFO_SIZE;
				var encryptedDataInfos = new byte[numEncryptedDataInfos][];

				uint offset = 8;
				for (int i = 0; i < numMethods; i++, offset += structSize) {
					uint methodBodyRva = readEncryptedUInt32(offset) - rvaDispl;
					uint totalSize = readEncryptedUInt32(offset + 4);
					uint methodInstructionRva = readEncryptedUInt32(offset + 8) - rvaDispl;

					var decryptedData = new byte[totalSize];

					// Read the method body header and method body (instrs + exception handlers).
					// The method body header is always in the first one. The instrs + ex handlers
					// are always in the last 4, and evenly divided (each byte[] is totalLen / 4).
					// The 2nd one is for the exceptions (or padding), but it may be null.
					uint offset2 = offset + 0xC;
					int exOffset = 0;
					for (int j = 0; j < encryptedDataInfos.Length; j++, offset2 += ENCRYPTED_DATA_INFO_SIZE) {
						// readByte(offset2); <-- index
						int encryptionType = readEncryptedInt16(offset2 + 1);
						uint dataOffset = readEncryptedUInt32(offset2 + 3);
						uint encryptedSize = readEncryptedUInt32(offset2 + 7);
						uint realSize = readEncryptedUInt32(offset2 + 11);
						if (j == 1)
							exOffset = readEncryptedInt32(offset2 + 15);
						if (j == 1 && exOffset == 0)
							encryptedDataInfos[j] = null;
						else
							encryptedDataInfos[j] = decrypt(encryptionType, dataOffset, encryptedSize, realSize);
					}

					int copyOffset = 0;
					copyOffset = copyData(decryptedData, encryptedDataInfos[0], copyOffset);
					for (int j = 2; j < encryptedDataInfos.Length; j++)
						copyOffset = copyData(decryptedData, encryptedDataInfos[j], copyOffset);
					copyData(decryptedData, encryptedDataInfos[1], exOffset); // Exceptions or padding

					var info = new DecryptedMethodInfo(methodBodyRva, decryptedData);
					infos[info.bodyRva] = info;
				}
			}

			static int copyData(byte[] dest, byte[] source, int offset) {
				if (source == null)
					return offset;
				Array.Copy(source, 0, dest, offset, source.Length);
				return offset + source.Length;
			}

			byte[] readData(uint offset, int size) {
				return peImage.offsetReadBytes(encryptedDataOffset + offset, size);
			}

			byte[] decrypt(int type, uint dataOffset, uint encryptedSize, uint realSize) {
				if (realSize == 0)
					return null;
				if (realSize > encryptedSize)
					throw new ApplicationException("Invalid realSize");

				var encrypted = readData(dataOffset, (int)encryptedSize);
				byte[] decrypted;
				switch (type) {
				case 1: decrypted = decrypt1(encrypted); break;
				case 2: decrypted = decrypt2(encrypted); break;
				case 3: decrypted = decrypt3(encrypted); break;
				case 4: decrypted = decrypt4(encrypted); break;
				case 5: decrypted = decrypt5(encrypted); break;
				case 6: decrypted = decrypt6(encrypted); break;
				case 7: decrypted = decrypt7(encrypted); break;
				default: throw new ApplicationException(string.Format("Invalid encryption type: {0:X2}", type));
				}

				if (realSize > decrypted.Length)
					throw new ApplicationException("Invalid decrypted length");
				Array.Resize(ref decrypted, (int)realSize);
				return decrypted;
			}

			byte[] decrypt1(byte[] encrypted) {
				var decrypted = new byte[encrypted.Length];
				for (int i = 0; i < decrypted.Length; i++)
					decrypted[i] = (byte)(encrypted[i] ^ mcHeader.readByte(i % 0x2000));
				return decrypted;
			}

			byte[] decrypt2(byte[] encrypted) {
				if ((encrypted.Length & 7) != 0)
					throw new ApplicationException("Invalid encryption #2 length");
				const int offset = 0x00FA;
				uint key4 = mcHeader.readUInt32(offset + 4 * 4);
				uint key5 = mcHeader.readUInt32(offset + 5 * 4);

				byte[] decrypted = new byte[encrypted.Length & ~7];
				var writer = new BinaryWriter(new MemoryStream(decrypted));

				int loopCount = encrypted.Length / 8;
				for (int i = 0; i < loopCount; i++) {
					uint val0 = BitConverter.ToUInt32(encrypted, i * 8);
					uint val1 = BitConverter.ToUInt32(encrypted, i * 8 + 4);
					uint x = (val1 >> 26) + (val0 << 6);
					uint y = (val0 >> 26) + (val1 << 6);

					writer.Write(x ^ key4);
					writer.Write(y ^ key5);
				}

				return decrypted;
			}

			static byte[] decrypt3Shifts = new byte[16] { 5, 11, 14, 21, 6, 20, 17, 29, 4, 10, 3, 2, 7, 1, 26, 18 };
			byte[] decrypt3(byte[] encrypted) {
				if ((encrypted.Length & 7) != 0)
					throw new ApplicationException("Invalid encryption #2 length");
				const int offset = 0x015E;
				uint key0 = mcHeader.readUInt32(offset + 0 * 4);
				uint key3 = mcHeader.readUInt32(offset + 3 * 4);

				byte[] decrypted = new byte[encrypted.Length & ~7];
				var writer = new BinaryWriter(new MemoryStream(decrypted));

				int loopCount = encrypted.Length / 8;
				for (int i = 0; i < loopCount; i++) {
					uint x = BitConverter.ToUInt32(encrypted, i * 8);
					uint y = BitConverter.ToUInt32(encrypted, i * 8 + 4);
					foreach (var shift in decrypt3Shifts) {
						int shift1 = 32 - shift;
						uint x1 = (y >> shift1) + (x << shift);
						uint y1 = (x >> shift1) + (y << shift);
						x = x1;
						y = y1;
					}

					writer.Write(x ^ key0);
					writer.Write(y ^ key3);
				}

				return decrypted;
			}

			byte[] decrypt4(byte[] encrypted) {
				var decrypted = new byte[encrypted.Length / 3 * 2 + 1];

				int count = encrypted.Length / 3;
				int i = 0, j = 0, k = 0;
				while (count-- > 0) {
					byte k1 = mcHeader.readByte(j + 1);
					byte k2 = mcHeader.readByte(j + 2);
					byte k3 = mcHeader.readByte(j + 3);
					decrypted[k++] = (byte)(((encrypted[i + 1] ^ k2) >> 4) | ((encrypted[i] ^ k1) & 0xF0));
					decrypted[k++] = (byte)(((encrypted[i + 1] ^ k2) << 4) + ((encrypted[i + 2] ^ k3) & 0x0F));
					i += 3;
					j = (j + 4) % 0x2000;
				}

				if ((encrypted.Length % 3) != 0)
					decrypted[k] = (byte)(encrypted[i] ^ mcHeader.readByte(j));

				return decrypted;
			}

			byte[] decrypt5(byte[] encrypted) {
				throw new NotImplementedException("Encryption type #5 not implemented yet");
			}

			byte[] decrypt6(byte[] encrypted) {
				throw new NotImplementedException("Encryption type #6 not implemented yet");
			}

			byte[] decrypt7(byte[] encrypted) {
				throw new NotImplementedException("Encryption type #7 not implemented yet");
			}
		}

		public FileDecrypter(MainType mainType) {
			this.mainType = mainType;
		}

		public bool decrypt(byte[] fileData, ref Dictionary<uint, DumpedMethod> dumpedMethods) {
			var peImage = new PeImage(fileData);
			var peHeader = new PeHeader(peImage);
			var mcHeader = new McHeader(peImage, peHeader);
			var methodInfos = new MethodInfos(peImage, peHeader, mcHeader);
			methodInfos.initializeInfos();

			dumpedMethods = new Dictionary<uint, DumpedMethod>();

			var metadataTables = peImage.Cor20Header.createMetadataTables();
			var methodDef = metadataTables.getMetadataType(MetadataIndex.iMethodDef);
			uint methodDefOffset = methodDef.fileOffset;
			for (int i = 0; i < methodDef.rows; i++, methodDefOffset += methodDef.totalSize) {
				uint bodyRva = peImage.offsetReadUInt32(methodDefOffset);
				if (bodyRva == 0)
					continue;

				var info = methodInfos.lookup(bodyRva);
				if (info == null)
					continue;

				uint bodyOffset = peImage.rvaToOffset(bodyRva);
				ushort magic = peImage.offsetReadUInt16(bodyOffset);
				if (magic != 0xFFF3)
					continue;

				var dm = new DumpedMethod();
				dm.token = (uint)(0x06000001 + i);
				dm.mdImplFlags = peImage.offsetReadUInt16(methodDefOffset + (uint)methodDef.fields[1].offset);
				dm.mdFlags = peImage.offsetReadUInt16(methodDefOffset + (uint)methodDef.fields[2].offset);
				dm.mdName = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[3].offset, methodDef.fields[3].size);
				dm.mdSignature = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[4].offset, methodDef.fields[4].size);
				dm.mdParamList = peImage.offsetRead(methodDefOffset + (uint)methodDef.fields[5].offset, methodDef.fields[5].size);

				var reader = new BinaryReader(new MemoryStream(info.body));
				byte b = reader.ReadByte();
				if ((b & 3) == 2) {
					dm.mhFlags = 2;
					dm.mhMaxStack = 8;
					dm.mhCodeSize = (uint)(b >> 2);
					dm.mhLocalVarSigTok = 0;
				}
				else {
					reader.BaseStream.Position--;
					dm.mhFlags = reader.ReadUInt16();
					dm.mhMaxStack = reader.ReadUInt16();
					dm.mhCodeSize = reader.ReadUInt32();
					dm.mhLocalVarSigTok = reader.ReadUInt32();
					uint codeOffset = (uint)(dm.mhFlags >> 12) * 4;
					reader.BaseStream.Position += codeOffset - 12;
				}

				dm.code = reader.ReadBytes((int)dm.mhCodeSize);
				if ((dm.mhFlags & 8) != 0) {
					reader.BaseStream.Position = (reader.BaseStream.Position + 3) & ~3;
					dm.extraSections = reader.ReadBytes((int)(reader.BaseStream.Length - reader.BaseStream.Position));
				}

				dumpedMethods[dm.token] = dm;
			}

			return true;
		}
	}
}
