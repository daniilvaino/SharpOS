using System;
using System.IO;
using System.Runtime;
using System.Text;
using System.Xml;
using SharpOS.AppSdk;

namespace XmlCheckApp
{
    // XMLTEST.EXE — System.Xml's reader (std-no-runtime/Xml) on the app tier:
    // nodes, attributes, namespaces, entities, CDATA, whitespace, typed content,
    // errors with positions. Expected values are what .NET gives for the same
    // calls. The exit code is the number of failed checks.
    internal static unsafe partial class AppEntry
    {
        private static int s_passed;
        private static int s_failed;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Main());
        }

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static void Check(string name, string expected, string actual)
        {
            bool ok = expected == actual;
            Check(name, ok);
            if (!ok)
            {
                Console.WriteLine("        expected: " + expected);
                Console.WriteLine("        actual:   " + actual);
            }
        }

        private static int Main()
        {
            Console.WriteLine("[xmlcheck] begin");
            Run("Nodes", Nodes);
            Run("Attributes", Attributes);
            Run("Namespaces", Namespaces);
            Run("Content", Content);
            Run("Errors", Errors);
            Console.WriteLine("==== " + s_passed.ToString() + "/" + (s_passed + s_failed).ToString() + " passed ====");
            return s_failed;
        }

        private static void Run(string name, Action body)
        {
            try { body(); }
            catch (Exception e)
            {
                Check(name + ": no exception (" + e.GetType().Name + ": " + e.Message + ")", false);
            }
        }

        private static XmlReader Reader(string text, XmlReaderSettings settings = null)
            => XmlReader.Create(new StringReader(text), settings ?? new XmlReaderSettings());

        // The node stream as .NET writes it for the same document: type:name=value.
        private static string Trace(string text, XmlReaderSettings settings = null)
        {
            var sb = new StringBuilder();
            using XmlReader r = Reader(text, settings);
            while (r.Read())
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Name(r.NodeType)).Append(':').Append(r.Name);
                if (r.HasValue) sb.Append('=').Append(r.Value.Replace("\n", "\\n"));
                if (r.IsEmptyElement) sb.Append("/");
            }
            return sb.ToString();
        }

        // The member's name by hand: an enum's ToString has no names to give in
        // an image (limits: Enum.GetNames is empty).
        private static string Name(XmlNodeType type) => type switch
        {
            XmlNodeType.Element => "Element",
            XmlNodeType.Text => "Text",
            XmlNodeType.CDATA => "CDATA",
            XmlNodeType.EntityReference => "EntityReference",
            XmlNodeType.ProcessingInstruction => "ProcessingInstruction",
            XmlNodeType.Comment => "Comment",
            XmlNodeType.DocumentType => "DocumentType",
            XmlNodeType.Whitespace => "Whitespace",
            XmlNodeType.SignificantWhitespace => "SignificantWhitespace",
            XmlNodeType.EndElement => "EndElement",
            XmlNodeType.XmlDeclaration => "XmlDeclaration",
            _ => ((int)type).ToString(),
        };

        private static void Nodes()
        {
            Check("element, text, end",
                  "Element:a Text:=hi EndElement:a",
                  Trace("<a>hi</a>"));
            Check("declaration, comment, PI, empty element",
                  "XmlDeclaration:xml=version=\"1.0\" Comment:= c  ProcessingInstruction:pi=x y Element:r Element:e/ EndElement:r",
                  Trace("<?xml version=\"1.0\"?><!-- c --><?pi x y?><r><e/></r>"));
            Check("CDATA and entities",
                  "Element:a CDATA:=<b>&amp; Text:= & < > ' \" A EndElement:a",
                  Trace("<a><![CDATA[<b>&amp;]]> &amp; &lt; &gt; &apos; &quot; &#65;</a>"));
            Check("whitespace kept, then ignored",
                  "Element:a Whitespace:=\\n   Element:b/ Whitespace:=\\n EndElement:a|Element:a Element:b/ EndElement:a",
                  Trace("<a>\n  <b/>\n</a>") + "|" + Trace("<a>\n  <b/>\n</a>", new XmlReaderSettings { IgnoreWhitespace = true }));
        }

        private static void Attributes()
        {
            using XmlReader r = Reader("<e a=\"1\" b='two &amp; 3' c=\"\"/>");
            r.Read();
            Check("attribute count", r.AttributeCount == 3);
            Check("GetAttribute by name", "two & 3", r.GetAttribute("b"));
            Check("missing attribute is null", r.GetAttribute("zz") == null);
            var sb = new StringBuilder();
            while (r.MoveToNextAttribute()) sb.Append(r.Name).Append('=').Append(r.Value).Append(';');
            Check("attributes in order", "a=1;b=two & 3;c=;", sb.ToString());
        }

        private static void Namespaces()
        {
            using XmlReader r = Reader("<x:root xmlns:x=\"urn:x\" xmlns=\"urn:d\"><x:a/><b/></x:root>");
            r.Read();
            Check("prefix, local name, namespace", "x|root|urn:x", r.Prefix + "|" + r.LocalName + "|" + r.NamespaceURI);
            r.Read();
            Check("prefixed child", "urn:x", r.NamespaceURI);
            r.Read();
            Check("default namespace child", "b|urn:d", r.LocalName + "|" + r.NamespaceURI);
            Check("LookupNamespace", "urn:x", r.LookupNamespace("x"));
        }

        private static void Content()
        {
            using XmlReader r = Reader("<r><i>42</i><d>2.5</d><b>true</b><s>a<!--x-->b</s></r>");
            r.ReadStartElement("r");
            Check("ReadElementContentAsInt", r.ReadElementContentAsInt() == 42);
            Check("ReadElementContentAsDouble", r.ReadElementContentAsDouble() == 2.5);
            Check("ReadElementContentAsBoolean", r.ReadElementContentAsBoolean());
            Check("ReadElementContentAsString skips comments", "ab", r.ReadElementContentAsString());
            Check("XmlConvert round trip", "1.5|True|7", XmlConvert.ToString(1.5) + "|" + (XmlConvert.ToBoolean("1") ? "True" : "False") + "|" + XmlConvert.ToInt32(" 7 ").ToString());
        }

        private static void Errors()
        {
            string message = "";
            int line = 0, position = 0;
            try
            {
                using XmlReader r = Reader("<a>\n  <b></a>");
                while (r.Read()) { }
            }
            catch (XmlException e)
            {
                message = e.Message;
                line = e.LineNumber;
                position = e.LinePosition;
            }
            Check("mismatched end tag: XmlException at 2,8 (" + line.ToString() + "," + position.ToString() + ")", line == 2 && position == 8);
            Check("message names both tags", message.Contains("'b'") && message.Contains("'a'"));
            bool dtd = false;
            try
            {
                using XmlReader r = Reader("<!DOCTYPE a [<!ENTITY e \"x\">]><a>&e;</a>");
                while (r.Read()) { }
            }
            catch (XmlException) { dtd = true; }
            Check("DTD prohibited by default (DtdProcessing.Prohibit)", dtd);
        }
    }
}
