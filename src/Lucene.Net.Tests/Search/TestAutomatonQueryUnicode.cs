using Lucene.Net.Documents;

namespace Lucene.Net.Search
{
    using NUnit.Framework;
    using Automaton = Lucene.Net.Util.Automaton.Automaton;
    using Directory = Lucene.Net.Store.Directory;

    /*
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

    using Document = Documents.Document;
    using Field = Field;
    using IndexReader = Lucene.Net.Index.IndexReader;
    using LuceneTestCase = Lucene.Net.Util.LuceneTestCase;
    using RandomIndexWriter = Lucene.Net.Index.RandomIndexWriter;
    using RegExp = Lucene.Net.Util.Automaton.RegExp;
    using Term = Lucene.Net.Index.Term;

    /// <summary>
    /// Test the automaton query for several unicode corner cases,
    /// specifically enumerating strings/indexes containing supplementary characters,
    /// and the differences between UTF-8/UTF-32 and UTF-16 binary sort order.
    /// </summary>
    [TestFixture]
    public class TestAutomatonQueryUnicode : LuceneTestCase
    {
        private IndexReader Reader;
        private IndexSearcher Searcher;
        private Directory Directory;

        private readonly string FN = "field";

        [SetUp]
        public override void SetUp()
        {
            base.SetUp();
            Directory = NewDirectory();
            RandomIndexWriter writer = new RandomIndexWriter(Random, Directory, Similarity, TimeZone);
            Document doc = new Document();
            Field titleField = NewTextField("title", "some title", Field.Store.NO);
            Field field = NewTextField(FN, "", Field.Store.NO);
            Field footerField = NewTextField("footer", "a footer", Field.Store.NO);
            doc.Add(titleField);
            doc.Add(field);
            doc.Add(footerField);
            field.SetStringValue("\uD866\uDF05abcdef");
            writer.AddDocument(doc);
            field.SetStringValue("\uD866\uDF06ghijkl");
            writer.AddDocument(doc);
            // this sorts before the previous two in UTF-8/UTF-32, but after in UTF-16!!!
            field.SetStringValue("\uFB94mnopqr");
            writer.AddDocument(doc);
            field.SetStringValue("\uFB95stuvwx"); // this one too.
            writer.AddDocument(doc);
            field.SetStringValue("a\uFFFCbc");
            writer.AddDocument(doc);
            field.SetStringValue("a\uFFFDbc");
            writer.AddDocument(doc);
            field.SetStringValue("a\uFFFEbc");
            writer.AddDocument(doc);
            field.SetStringValue("a\uFB94bc");
            writer.AddDocument(doc);
            field.SetStringValue("bacadaba");
            writer.AddDocument(doc);
            field.SetStringValue("\uFFFD");
            writer.AddDocument(doc);
            field.SetStringValue("\uFFFD\uD866\uDF05");
            writer.AddDocument(doc);
            field.SetStringValue("\uFFFD\uFFFD");
            writer.AddDocument(doc);
            Reader = writer.GetReader();
            Searcher = NewSearcher(Reader);
            writer.Dispose();
        }

        [TearDown]
        public override void TearDown()
        {
            Reader.Dispose();
            Directory.Dispose();
            base.TearDown();
        }

        private Term NewTerm(string value)
        {
            return new Term(FN, value);
        }

        private int AutomatonQueryNrHits(AutomatonQuery query)
        {
            return Searcher.Search(query, 5).TotalHits;
        }

        private void AssertAutomatonHits(int expected, Automaton automaton)
        {
            AutomatonQuery query = new AutomatonQuery(NewTerm("bogus"), automaton);

            query.MultiTermRewriteMethod = (MultiTermQuery.SCORING_BOOLEAN_QUERY_REWRITE);
            Assert.AreEqual(expected, AutomatonQueryNrHits(query));

            query.MultiTermRewriteMethod = (MultiTermQuery.CONSTANT_SCORE_FILTER_REWRITE);
            Assert.AreEqual(expected, AutomatonQueryNrHits(query));

            query.MultiTermRewriteMethod = (MultiTermQuery.CONSTANT_SCORE_BOOLEAN_QUERY_REWRITE);
            Assert.AreEqual(expected, AutomatonQueryNrHits(query));

            query.MultiTermRewriteMethod = (MultiTermQuery.CONSTANT_SCORE_AUTO_REWRITE_DEFAULT);
            Assert.AreEqual(expected, AutomatonQueryNrHits(query));
        }

        /// <summary>
        /// Test that AutomatonQuery interacts with lucene's sort order correctly.
        ///
        /// this expression matches something either starting with the arabic
        /// presentation forms block, or a supplementary character.
        /// </summary>
        [Test]
        public virtual void TestSortOrder()
        {
            Automaton a = (new RegExp("((\uD866\uDF05)|\uFB94).*")).ToAutomaton();
            AssertAutomatonHits(2, a);
        }
    }
}