﻿/*
 * Licensed to the Apache Software Foundation (ASF) under one or more
 * contributor license agreements.  See the NOTICE file distributed with
 * this work for additional information regarding copyright ownership.
 * The ASF licenses this file to You under the Apache License, Version 2.0
 * (the "License"); you may not use this file except in compliance with
 * the License.  You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Lucene.Net.Codecs.Sep;
using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Support;
using Lucene.Net.Util;
using Lucene.Net.Util.Fst;
using Lucene.Net.Util.Packed;

namespace Lucene.Net.Codecs.Memory
{
    /// <summary>
    /// Reader for <seealso cref="MemoryDocValuesFormat"/>
    /// </summary>
    internal class MemoryDocValuesProducer : DocValuesProducer
    {
        // metadata maps (just file pointers and minimal stuff)
        private readonly IDictionary<int?, NumericEntry> numerics;
        private readonly IDictionary<int?, BinaryEntry> binaries;
        private readonly IDictionary<int?, FSTEntry> fsts;
        private readonly IndexInput data;

        // ram instances we have already loaded
        private readonly IDictionary<int?, NumericDocValues> numericInstances = new Dictionary<int?, NumericDocValues>();
        private readonly IDictionary<int?, BinaryDocValues> binaryInstances = new Dictionary<int?, BinaryDocValues>();
        private readonly IDictionary<int?, FST<long?>> fstInstances = new Dictionary<int?, FST<long?>>();
        private readonly IDictionary<int?, Bits> docsWithFieldInstances = new Dictionary<int?, Bits>();

        private readonly int maxDoc;
        private readonly AtomicLong ramBytesUsed;
        private readonly int version;

        internal const byte NUMBER = 0;
        internal const byte BYTES = 1;
        //internal const sbyte org;

        internal const int BLOCK_SIZE = 4096;

        internal const byte DELTA_COMPRESSED = 0;
        internal const byte TABLE_COMPRESSED = 1;
        internal const byte UNCOMPRESSED = 2;
        internal const byte GCD_COMPRESSED = 3;

        internal const int VERSION_START = 0;
        internal const int VERSION_GCD_COMPRESSION = 1;
        internal const int VERSION_CHECKSUM = 2;
        internal const int VERSION_CURRENT = VERSION_CHECKSUM;


        internal MemoryDocValuesProducer(SegmentReadState state, string dataCodec, string dataExtension,
            string metaCodec, string metaExtension)
        {
            maxDoc = state.SegmentInfo.DocCount;
            var metaName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix, metaExtension);
            // read in the entries from the metadata file.
            var @in = state.Directory.OpenChecksumInput(metaName, state.Context);
            bool success = false;
            try
            {
                version = CodecUtil.CheckHeader(@in, metaCodec, VERSION_START, VERSION_CURRENT);
                numerics = new Dictionary<int?, NumericEntry>();
                binaries = new Dictionary<int?, BinaryEntry>();
                fsts = new Dictionary<int?, FSTEntry>();
                ReadFields(@in, state.FieldInfos);
                if (version >= VERSION_CHECKSUM)
                {
                    CodecUtil.CheckFooter(@in);
                }
                else
                {
                    CodecUtil.CheckEOF(@in);
                }
                ramBytesUsed = new AtomicLong(RamUsageEstimator.ShallowSizeOfInstance(this.GetType()));
                success = true;
            }
            finally
            {
                if (success)
                {
                    IOUtils.Close(@in);
                }
                else
                {
                    IOUtils.CloseWhileHandlingException(@in);
                }
            }

            success = false;
            try
            {
                string dataName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix,
                    dataExtension);
                data = state.Directory.OpenInput(dataName, state.Context);

                int version2 = CodecUtil.CheckHeader(data, dataCodec, VERSION_START, VERSION_CURRENT);
                if (version != version2)
                {
                    throw new CorruptIndexException("Format versions mismatch");
                }

                success = true;
            }
            finally
            {
                if (!success)
                {
                    IOUtils.CloseWhileHandlingException(this.data);
                }
            }
        }

        private void ReadFields(IndexInput meta, FieldInfos infos)
        {
            int fieldNumber = meta.ReadVInt();
            while (fieldNumber != -1)
            {
                int fieldType = meta.ReadByte();
                if (fieldType == NUMBER)
                {
                    NumericEntry entry = new NumericEntry();
                    entry.offset = meta.ReadLong();
                    entry.missingOffset = meta.ReadLong();
                    if (entry.missingOffset != -1)
                    {
                        entry.missingBytes = meta.ReadLong();
                    }
                    else
                    {
                        entry.missingBytes = 0;
                    }
                    entry.format = meta.ReadByte();
                    switch (entry.format)
                    {
                        case DELTA_COMPRESSED:
                        case TABLE_COMPRESSED:
                        case GCD_COMPRESSED:
                        case UNCOMPRESSED:
                            break;
                        default:
                            throw new CorruptIndexException("Unknown format: " + entry.format + ", input=" + meta);
                    }
                    if (entry.format != UNCOMPRESSED)
                    {
                        entry.packedIntsVersion = meta.ReadVInt();
                    }
                    numerics[fieldNumber] = entry;
                }
                else if (fieldType == BYTES)
                {
                    BinaryEntry entry = new BinaryEntry();
                    entry.offset = meta.ReadLong();
                    entry.numBytes = meta.ReadLong();
                    entry.missingOffset = meta.ReadLong();
                    if (entry.missingOffset != -1)
                    {
                        entry.missingBytes = meta.ReadLong();
                    }
                    else
                    {
                        entry.missingBytes = 0;
                    }
                    entry.minLength = meta.ReadVInt();
                    entry.maxLength = meta.ReadVInt();
                    if (entry.minLength != entry.maxLength)
                    {
                        entry.packedIntsVersion = meta.ReadVInt();
                        entry.blockSize = meta.ReadVInt();
                    }
                    binaries[fieldNumber] = entry;
                }
                else if (fieldType == FST)
                {
                    var entry = new FSTEntry {offset = meta.ReadLong(), numOrds = meta.ReadVLong()};
                    fsts[fieldNumber] = entry;
                }
                else
                {
                    throw new CorruptIndexException("invalid entry type: " + fieldType + ", input=" + meta);
                }
                fieldNumber = meta.ReadVInt();
            }
        }

        public override NumericDocValues GetNumeric(FieldInfo field)
        {
            lock (this)
            {
                NumericDocValues instance = numericInstances[field.Number];
                if (instance == null)
                {
                    instance = loadNumeric(field);
                    numericInstances[field.Number] = instance;
                }
                return instance;
            }
        }

        public override long RamBytesUsed()
        {
            return ramBytesUsed.Get();
        }

        public override void CheckIntegrity()
        {
            if (version >= VERSION_CHECKSUM)
            {
                CodecUtil.ChecksumEntireFile(data);
            }
        }

        private NumericDocValues LoadNumeric(FieldInfo field)
        {
            NumericEntry entry = numerics[field.Number];
            data.Seek(entry.offset + entry.missingBytes);
            switch (entry.format)
            {
                case TABLE_COMPRESSED:
                    int size = data.ReadVInt();
                    if (size > 256)
                    {
                        throw new CorruptIndexException(
                            "TABLE_COMPRESSED cannot have more than 256 distinct values, input=" + data);
                    }
                    var decode = new long[size];
                    for (int i = 0; i < decode.Length; i++)
                    {
                        decode[i] = data.ReadLong();
                    }
                    int formatID = data.ReadVInt();
                    int bitsPerValue = data.ReadVInt();
                    PackedInts.Reader ordsReader = PackedInts.GetReaderNoHeader(data, PackedInts.Format.byId(formatID),
                        entry.packedIntsVersion, maxDoc, bitsPerValue);
                    ramBytesUsed.AddAndGet(RamUsageEstimator.SizeOf(decode) + ordsReader.RamBytesUsed());
                    return new NumericDocValuesAnonymousInnerClassHelper(this, decode, ordsReader);
                case DELTA_COMPRESSED:
                    int blockSize = data.ReadVInt();
                    var reader = new BlockPackedReader(data, entry.packedIntsVersion, blockSize, maxDoc,
                        false);
                    ramBytesUsed.AddAndGet(reader.RamBytesUsed());
                    return reader;
                case UNCOMPRESSED:
                    var bytes = new byte[maxDoc];
                    data.ReadBytes(bytes, 0, bytes.Length);
                    ramBytesUsed.AddAndGet(RamUsageEstimator.SizeOf(bytes));
                    return new NumericDocValuesAnonymousInnerClassHelper2(this, bytes);
                case GCD_COMPRESSED:
                    long min = data.ReadLong();
                    long mult = data.ReadLong();
                    int quotientBlockSize = data.ReadVInt();
                    var quotientReader = new BlockPackedReader(data, entry.packedIntsVersion,
                        quotientBlockSize, maxDoc, false);
                    ramBytesUsed.AddAndGet(quotientReader.RamBytesUsed());
                    return new NumericDocValuesAnonymousInnerClassHelper3(this, min, mult, quotientReader);
                default:
                    throw new AssertionError();
            }
        }

        private class NumericDocValuesAnonymousInnerClassHelper : NumericDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private readonly long[] decode;
            private readonly IntIndexInput.Reader ordsReader;

            public NumericDocValuesAnonymousInnerClassHelper(MemoryDocValuesProducer outerInstance, long[] decode,
                IntIndexInput.Reader ordsReader)
            {
                this.outerInstance = outerInstance;
                this.decode = decode;
                this.ordsReader = ordsReader;
            }

            public override long Get(int docID)
            {
                return decode[(int) ordsReader.Get(docID)];
            }
        }

        private class NumericDocValuesAnonymousInnerClassHelper2 : NumericDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;
            private readonly byte[] bytes;

            public NumericDocValuesAnonymousInnerClassHelper2(MemoryDocValuesProducer outerInstance, byte[] bytes)
            {
                this.outerInstance = outerInstance;
                this.bytes = bytes;
            }

            public override long Get(int docID)
            {
                return bytes[docID];
            }
        }

        private class NumericDocValuesAnonymousInnerClassHelper3 : NumericDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private readonly long min;
            private readonly long mult;
            private readonly BlockPackedReader quotientReader;

            public NumericDocValuesAnonymousInnerClassHelper3(MemoryDocValuesProducer outerInstance, long min, long mult,
                BlockPackedReader quotientReader)
            {
                this.outerInstance = outerInstance;
                this.min = min;
                this.mult = mult;
                this.quotientReader = quotientReader;
            }

            public override long Get(int docID)
            {
                return min + mult*quotientReader.Get(docID);
            }
        }

        public override BinaryDocValues GetBinary(FieldInfo field)
        {
            lock (this)
            {
                BinaryDocValues instance = binaryInstances[field.Number];
                if (instance == null)
                {
                    instance = loadBinary(field);
                    binaryInstances[field.Number] = instance;
                }
                return instance;
            }
        }

        private BinaryDocValues LoadBinary(FieldInfo field)
        {
            BinaryEntry entry = binaries[field.Number];
            data.Seek(entry.offset);
            var bytes = new PagedBytes(16);
            bytes.Copy(data, entry.numBytes);
            PagedBytes.Reader bytesReader = bytes.Freeze(true);
            if (entry.minLength == entry.maxLength)
            {
                int fixedLength = entry.minLength;
                ramBytesUsed.AddAndGet(bytes.RamBytesUsed());
                return new BinaryDocValuesAnonymousInnerClassHelper(this, bytesReader, fixedLength);
            }
            else
            {
                data.Seek(data.FilePointer + entry.missingBytes);
                var addresses = new MonotonicBlockPackedReader(data, entry.packedIntsVersion,
                    entry.blockSize, maxDoc, false);
                ramBytesUsed.AddAndGet(bytes.RamBytesUsed() + addresses.RamBytesUsed());
                return new BinaryDocValuesAnonymousInnerClassHelper2(this, bytesReader, addresses);
            }
        }

        private class BinaryDocValuesAnonymousInnerClassHelper : BinaryDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private readonly IntIndexInput.Reader bytesReader;
            private readonly int fixedLength;

            public BinaryDocValuesAnonymousInnerClassHelper(MemoryDocValuesProducer outerInstance,
                IntIndexInput.Reader bytesReader, int fixedLength)
            {
                this.outerInstance = outerInstance;
                this.bytesReader = bytesReader;
                this.fixedLength = fixedLength;
            }

            public override void Get(int docID, BytesRef result)
            {
                bytesReader.FillSlice(result, fixedLength*(long) docID, fixedLength);
            }
        }

        private class BinaryDocValuesAnonymousInnerClassHelper2 : BinaryDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private readonly IntIndexInput.Reader bytesReader;
            private readonly MonotonicBlockPackedReader addresses;

            public BinaryDocValuesAnonymousInnerClassHelper2(MemoryDocValuesProducer outerInstance,
                IntIndexInput.Reader bytesReader, MonotonicBlockPackedReader addresses)
            {
                this.outerInstance = outerInstance;
                this.bytesReader = bytesReader;
                this.addresses = addresses;
            }

            public override void Get(int docID, BytesRef result)
            {
                long startAddress = docID == 0 ? 0 : addresses.Get(docID - 1);
                long endAddress = addresses.Get(docID);
                bytesReader.FillSlice(result, startAddress, (int) (endAddress - startAddress));
            }
        }

        public override SortedDocValues GetSorted(FieldInfo field)
        {
            FSTEntry entry = fsts[field.number];
            if (entry.numOrds == 0)
            {
                return DocValues.EMPTY_SORTED;
            }
            FST<long?> instance;
            lock (this)
            {
                instance = fstInstances[field.number];
                if (instance == null)
                {
                    data.seek(entry.offset);
                    instance = new FST<>(data, PositiveIntOutputs.Singleton);
                    ramBytesUsed.addAndGet(instance.sizeInBytes());
                    fstInstances[field.number] = instance;
                }
            }
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final index.NumericDocValues docToOrd = getNumeric(field);
            NumericDocValues docToOrd = getNumeric(field);
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST<Long> fst = instance;
            FST<long?> fst = instance;

            // per-thread resources
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.BytesReader in = fst.getBytesReader();
            FST.BytesReader @in = fst.BytesReader;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.Arc<Long> firstArc = new util.fst.FST.Arc<>();
            FST.Arc<long?> firstArc = new FST.Arc<long?>();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.Arc<Long> scratchArc = new util.fst.FST.Arc<>();
            FST.Arc<long?> scratchArc = new FST.Arc<long?>();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.IntsRef scratchInts = new util.IntsRef();
            IntsRef scratchInts = new IntsRef();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.BytesRefFSTEnum<Long> fstEnum = new util.fst.BytesRefFSTEnum<>(fst);
            BytesRefFSTEnum<long?> fstEnum = new BytesRefFSTEnum<long?>(fst);

            return new SortedDocValuesAnonymousInnerClassHelper(this, entry, docToOrd, fst, @in, firstArc, scratchArc,
                scratchInts, fstEnum);
        }

        private class SortedDocValuesAnonymousInnerClassHelper : SortedDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private MemoryDocValuesProducer.FSTEntry entry;
            private NumericDocValues docToOrd;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST<long?> fst;
            private FST<long?> fst;
            private FST.BytesReader @in;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST.Arc<long?> firstArc;
            private FST.Arc<long?> firstArc;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST.Arc<long?> scratchArc;
            private FST.Arc<long?> scratchArc;
            private IntsRef scratchInts;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.BytesRefFSTEnum<long?> fstEnum;
            private BytesRefFSTEnum<long?> fstEnum;

            public SortedDocValuesAnonymousInnerClassHelper<T1, T2, T3, T4> 
        (
            private MemoryDocValuesProducer outerInstance, org
        .
            private MemoryDocValuesProducer.FSTEntry entry, NumericDocValues
            private docToOrd 
        ,
            private FST<T1> fst, FST
        .
            private FST.BytesReader @in, FST
        .
            private Arc<T2> firstArc, FST
        .
            private Arc<T3> scratchArc, IntsRef
            private scratchInts 
        ,
            private BytesRefFSTEnum<T4> fstEnum 
        )
        {
            this.outerInstance = outerInstance;
            this.entry = entry;
            this.docToOrd = docToOrd;
            this.fst = fst;
            this.@in = @in;
            this.firstArc = firstArc;
            this.scratchArc = scratchArc;
            this.scratchInts = scratchInts;
            this.fstEnum = fstEnum;
        }

            public override int getOrd(int docID)
            {
                return (int) docToOrd.get(docID);
            }

            public override void lookupOrd(int ord, BytesRef result)
            {
                try
                {
                    @in.Position = 0;
                    fst.getFirstArc(firstArc);
                    IntsRef output = Util.getByOutput(fst, ord, @in, firstArc, scratchArc, scratchInts);
                    result.bytes = new sbyte[output.length];
                    result.offset = 0;
                    result.length = 0;
                    Util.toBytesRef(output, result);
                }
                catch (IOException bogus)
                {
                    throw new Exception(bogus);
                }
            }

            public override int LookupTerm(BytesRef key)
            {
                try
                {
                    BytesRefFSTEnum.InputOutput<long?> o = fstEnum.seekCeil(key);
                    if (o == null)
                    {
                        return -ValueCount - 1;
                    }
                    else if (o.input.Equals(key))
                    {
                        return (int) o.output;
                    }
                    else
                    {
                        return (int) -o.output - 1;
                    }
                }
                catch (IOException bogus)
                {
                    throw new Exception(bogus);
                }
            }

            public override int ValueCount
            {
                get { return (int) entry.numOrds; }
            }

            public override TermsEnum TermsEnum()
            {
                return new FSTTermsEnum(fst);
            }
        }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: @Override public index.SortedSetDocValues getSortedSet(index.FieldInfo field) throws java.io.IOException
        public override SortedSetDocValues getSortedSet(FieldInfo field)
        {
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final FSTEntry entry = fsts.get(field.number);
            FSTEntry entry = fsts[field.number];
            if (entry.numOrds == 0)
            {
                return DocValues.EMPTY_SORTED_SET; // empty FST!
            }
            FST<long?> instance;
            lock (this)
            {
                instance = fstInstances[field.number];
                if (instance == null)
                {
                    data.seek(entry.offset);
                    instance = new FST<>(data, PositiveIntOutputs.Singleton);
                    ramBytesUsed.addAndGet(instance.sizeInBytes());
                    fstInstances[field.number] = instance;
                }
            }
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final index.BinaryDocValues docToOrds = getBinary(field);
            BinaryDocValues docToOrds = GetBinary(field);
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST<Long> fst = instance;
            FST<long?> fst = instance;

            // per-thread resources
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.BytesReader in = fst.getBytesReader();
            FST.BytesReader @in = fst.BytesReader;
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.Arc<Long> firstArc = new util.fst.FST.Arc<>();
            FST.Arc<long?> firstArc = new FST.Arc<long?>();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.FST.Arc<Long> scratchArc = new util.fst.FST.Arc<>();
            FST.Arc<long?> scratchArc = new FST.Arc<long?>();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.IntsRef scratchInts = new util.IntsRef();
            IntsRef scratchInts = new IntsRef();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.fst.BytesRefFSTEnum<Long> fstEnum = new util.fst.BytesRefFSTEnum<>(fst);
            BytesRefFSTEnum<long?> fstEnum = new BytesRefFSTEnum<long?>(fst);
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final util.BytesRef ref = new util.BytesRef();
            BytesRef @ref = new BytesRef();
//JAVA TO C# CONVERTER WARNING: The original Java variable was marked 'final':
//ORIGINAL LINE: final store.ByteArrayDataInput input = new store.ByteArrayDataInput();
            ByteArrayDataInput input = new ByteArrayDataInput();
            return new SortedSetDocValuesAnonymousInnerClassHelper(this, entry, docToOrds, fst, @in, firstArc,
                scratchArc, scratchInts, fstEnum, @ref, input);
        }

        private class SortedSetDocValuesAnonymousInnerClassHelper : SortedSetDocValues
        {
            private readonly MemoryDocValuesProducer outerInstance;

            private MemoryDocValuesProducer.FSTEntry entry;
            private BinaryDocValues docToOrds;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST<long?> fst;
            private FST<long?> fst;
            private FST.BytesReader @in;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST.Arc<long?> firstArc;
            private FST.Arc<long?> firstArc;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.FST.Arc<long?> scratchArc;
            private FST.Arc<long?> scratchArc;
            private IntsRef scratchInts;
//JAVA TO C# CONVERTER TODO TASK: Java wildcard generics are not converted to .NET:
//ORIGINAL LINE: private util.fst.BytesRefFSTEnum<long?> fstEnum;
            private BytesRefFSTEnum<long?> fstEnum;
            private BytesRef @ref;
            private ByteArrayDataInput input;

            public SortedSetDocValuesAnonymousInnerClassHelper<T1, T2, T3, T4> 
        (
            private MemoryDocValuesProducer outerInstance, org
        .
            private MemoryDocValuesProducer.FSTEntry entry, BinaryDocValues
            private docToOrds 
        ,
            private FST<T1> fst, FST
        .
            private BytesReader @in, FST
        .
            private Arc<T2> firstArc, FST
        .
            private Arc<T3> scratchArc, IntsRef
            private scratchInts 
        ,
            private BytesRefFSTEnum<T4> fstEnum, BytesRef
            private @ref 
        ,
            private ByteArrayDataInput input 
        )
        {
            this.outerInstance = outerInstance;
            this.entry = entry;
            this.docToOrds = docToOrds;
            this.fst = fst;
            this.@in = @in;
            this.firstArc = firstArc;
            this.scratchArc = scratchArc;
            this.scratchInts = scratchInts;
            this.fstEnum = fstEnum;
            this.@ref = @ref;
            this.input = input;
        }

            internal long currentOrd;

            public override long nextOrd()
            {
                if (input.eof())
                {
                    return NO_MORE_ORDS;
                }
                else
                {
                    currentOrd += input.ReadVLong();
                    return currentOrd;
                }
            }

            public override int Document
            {
                set
                {
                    docToOrds.get(value, @ref);
                    input.reset(@ref.bytes, @ref.offset, @ref.length);
                    currentOrd = 0;
                }
            }

            public override void lookupOrd(long ord, BytesRef result)
            {
                try
                {
                    @in.Position = 0;
                    fst.getFirstArc(firstArc);
                    IntsRef output = Util.getByOutput(fst, ord, @in, firstArc, scratchArc, scratchInts);
                    result.bytes = new sbyte[output.length];
                    result.offset = 0;
                    result.length = 0;
                    Util.toBytesRef(output, result);
                }
                catch (IOException bogus)
                {
                    throw new Exception(bogus);
                }
            }

            public override long LookupTerm(BytesRef key)
            {
                try
                {
                    BytesRefFSTEnum.InputOutput<long?> o = fstEnum.seekCeil(key);
                    if (o == null)
                    {
                        return -ValueCount - 1;
                    }
                    else if (o.input.Equals(key))
                    {
                        return (int) o.output;
                    }
                    else
                    {
                        return -o.output - 1;
                    }
                }
                catch (IOException bogus)
                {
                    throw new Exception(bogus);
                }
            }

            public override long ValueCount
            {
                get { return entry.numOrds; }
            }

            public override TermsEnum termsEnum()
            {
                return new FSTTermsEnum(fst);
            }
        }

//JAVA TO C# CONVERTER WARNING: Method 'throws' clauses are not available in .NET:
//ORIGINAL LINE: private util.Bits getMissingBits(int fieldNumber, final long offset, final long length) throws java.io.IOException
//JAVA TO C# CONVERTER WARNING: 'final' parameters are not available in .NET:
        private Bits getMissingBits(int fieldNumber, long offset, long length)
        {
            if (offset == -1)
            {
                return new Bits.MatchAllBits(maxDoc);
            }
            else
            {
                Bits instance;
                lock (this)
                {
                    instance = docsWithFieldInstances[fieldNumber];
                    if (instance == null)
                    {
                        IndexInput data = this.data.clone();
                        data.seek(offset);
                        Debug.Assert(length%8 == 0);
                        long[] bits = new long[(int) length >> 3];
                        for (int i = 0; i < bits.Length; i++)
                        {
                            bits[i] = data.ReadLong();
                        }
                        instance = new FixedBitSet(bits, maxDoc);
                        docsWithFieldInstances[fieldNumber] = instance;
                    }
                }
                return instance;
            }
        }

        public override Bits GetDocsWithField(FieldInfo field)
        {
            switch (field.DocValuesType)
            {
                case SORTED_SET:
                    return DocValues.docsWithValue(getSortedSet(field), maxDoc);
                case SORTED:
                    return DocValues.docsWithValue(getSorted(field), maxDoc);
                case BINARY:
                    BinaryEntry be = binaries[field.number];
                    return getMissingBits(field.number, be.missingOffset, be.missingBytes);
                case NUMERIC:
                    NumericEntry ne = numerics[field.number];
                    return getMissingBits(field.number, ne.missingOffset, ne.missingBytes);
                default:
                    throw new AssertionError();
            }
        }

        public override void Close()
        {
            data.Close();
        }

        internal class NumericEntry
        {
            internal long offset;
            internal long missingOffset;
            internal long missingBytes;
            internal byte format;
            internal int packedIntsVersion;
        }

        internal class BinaryEntry
        {
            internal long offset;
            internal long missingOffset;
            internal long missingBytes;
            internal long numBytes;
            internal int minLength;
            internal int maxLength;
            internal int packedIntsVersion;
            internal int blockSize;
        }

        internal class FSTEntry
        {
            internal long offset;
            internal long numOrds;
        }

        // exposes FSTEnum directly as a TermsEnum: avoids binary-search next()
        internal class FSTTermsEnum : TermsEnum
        {
            internal readonly BytesRefFSTEnum<long?> @in;

            // this is all for the complicated seek(ord)...
            // maybe we should add a FSTEnum that supports this operation?
            internal readonly FST<long?> fst;
            internal readonly FST.BytesReader bytesReader;
            internal readonly FST.Arc<long?> firstArc = new FST.Arc<long?>();
            internal readonly FST.Arc<long?> scratchArc = new FST.Arc<long?>();
            internal readonly IntsRef scratchInts = new IntsRef();
            internal readonly BytesRef scratchBytes = new BytesRef();

            internal FSTTermsEnum(FST<long?> fst)
            {
                this.fst = fst;
                @in = new BytesRefFSTEnum<>(fst);
                bytesReader = fst.BytesReader;
            }

            public override BytesRef Next()
            {
                BytesRefFSTEnum.InputOutput<long?> io = @in.Next();
                return io == null ? null : io.Input;
            }

            public override IComparer<BytesRef> Comparator
            {
                get { return BytesRef.UTF8SortedAsUnicodeComparer; }
            }

            public override SeekStatus SeekCeil(BytesRef text)
            {
                if (@in.SeekCeil(text) == null)
                {
                    return SeekStatus.END;
                }
                else if (Term().Equals(text))
                {
                    // TODO: add SeekStatus to FSTEnum like in https://issues.apache.org/jira/browse/LUCENE-3729
                    // to remove this comparision?
                    return SeekStatus.FOUND;
                }
                else
                {
                    return SeekStatus.NOT_FOUND;
                }
            }

            public override bool SeekExact(BytesRef text)
            {
                return @in.SeekExact(text) != null;
            }

            public override void SeekExact(long ord)
            {
                // TODO: would be better to make this simpler and faster.
                // but we dont want to introduce a bug that corrupts our enum state!
                bytesReader.Position = 0;
                fst.GetFirstArc(firstArc);
                IntsRef output = Util.GetByOutput(fst, ord, bytesReader, firstArc, scratchArc, scratchInts);
                scratchBytes.Bytes = new sbyte[output.Length];
                scratchBytes.Offset = 0;
                scratchBytes.Length = 0;
                Util.ToBytesRef(output, scratchBytes);
                // TODO: we could do this lazily, better to try to push into FSTEnum though?
                @in.SeekExact(scratchBytes);
            }

            public override BytesRef Term()
            {
                return @in.Current().Input;
            }

            public override long Ord()
            {
                return @in.Current().Output;
            }

            public override int DocFreq()
            {
                throw new NotSupportedException();
            }

            public override long TotalTermFreq()
            {
                throw new NotSupportedException();
            }

            public override DocsEnum Docs(Bits liveDocs, DocsEnum reuse, int flags)
            {
                throw new NotSupportedException();
            }

            public override DocsAndPositionsEnum DocsAndPositions(Bits liveDocs, DocsAndPositionsEnum reuse, int flags)
            {
                throw new NotSupportedException();
            }
        }
    }

}