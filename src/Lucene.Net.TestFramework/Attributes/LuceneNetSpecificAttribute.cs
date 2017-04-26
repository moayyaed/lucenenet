/*
 *
 * Licensed to the Apache Software Foundation (ASF) under one
 * or more contributor license agreements.  See the NOTICE file
 * distributed with this work for additional information
 * regarding copyright ownership.  The ASF licenses this file
 * to you under the Apache License, Version 2.0 (the
 * "License"); you may not use this file except in compliance
 * with the License.  You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing,
 * software distributed under the License is distributed on an
 * "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY
 * KIND, either express or implied.  See the License for the
 * specific language governing permissions and limitations
 * under the License.
 *
*/

using NUnit.Framework;

namespace Lucene.Net.Attributes
{
    /// <summary>
    /// This test was added during the port to .NET to test
    /// additional factors that apply specifically to the port.
    /// In other words, apply this attribute to the test if it
    /// did not exist in Java Lucene.
    /// </summary>
    public class LuceneNetSpecificAttribute : CategoryAttribute
    {
        public LuceneNetSpecificAttribute()
            : base("LUCENENET")
        {
            // nothing to do here but invoke the base contsructor.
        }
    }
}
