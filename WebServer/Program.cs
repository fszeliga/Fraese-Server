﻿using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using De.Boenigk.SMC5D.Basics;
using System.Xml;
using System.Xml.Serialization;
using De.Boenigk.SMC5D.Settings;

namespace WebServer
{
    class Program
    {

        static int Main(string[] args)
        {
            HttpServer httpServer;
            if (args.GetLength(0) > 0)
            {
                httpServer = new MyHttpServer(Convert.ToString(args[0]), Convert.ToInt16(args[1]));
            }
            else
            {
                httpServer = new MyHttpServer("127.0.0.1", 1337);
            }
            Thread thread = new Thread(new ThreadStart(httpServer.listen));
            thread.Start();

            return 0;
        }
    }


    public class HttpProcessor
    {
        public TcpClient socket;
        public HttpServer srv;

        private Stream inputStream;
        public StreamWriter outputStream;

        public String http_method;
        public String http_url;
        public String http_protocol_versionstring;
        public Hashtable httpHeaders = new Hashtable();


        private static int MAX_POST_SIZE = 10 * 1024 * 1024; // 10MB

        public HttpProcessor(TcpClient s, HttpServer srv)
        {
            this.socket = s;
            this.srv = srv;
        }


        private string streamReadLine(Stream inputStream)
        {
            int next_char;
            string data = "";
            while (true)
            {
                next_char = inputStream.ReadByte();
                if (next_char == '\n') { break; }
                if (next_char == '\r') { continue; }
                if (next_char == -1) { Thread.Sleep(1); continue; };
                data += Convert.ToChar(next_char);
            }
            return data;
        }
        public void process()
        {
            // we can't use a StreamReader for input, because it buffers up extra data on us inside it's
            // "processed" view of the world, and we want the data raw after the headers
            inputStream = new BufferedStream(socket.GetStream());

            // we probably shouldn't be using a streamwriter for all output from handlers either
            outputStream = new StreamWriter(new BufferedStream(socket.GetStream()));
            try
            {
                parseRequest();
                readHeaders();
                if (http_method.Equals("GET"))
                {
                    handleGETRequest();
                }
                else if (http_method.Equals("POST"))
                {
                    handlePOSTRequest();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("Exception: " + e.ToString());
                writeFailure();
            }
            outputStream.Flush();
            // bs.Flush(); // flush any remaining output
            inputStream = null; outputStream = null; // bs = null;            
            socket.Close();
        }

        public void parseRequest()
        {
            String request = streamReadLine(inputStream);
            string[] tokens = request.Split(' ');
            if (tokens.Length != 3)
            {
                throw new Exception("invalid http request line");
            }
            http_method = tokens[0].ToUpper();
            http_url = tokens[1];
            http_protocol_versionstring = tokens[2];

            Console.WriteLine("starting: " + request);
        }

        public void readHeaders()
        {
            Console.WriteLine("readHeaders()");
            String line;
            while ((line = streamReadLine(inputStream)) != null)
            {
                if (line.Equals(""))
                {
                    Console.WriteLine("got headers");
                    return;
                }

                int separator = line.IndexOf(':');
                if (separator == -1)
                {
                    throw new Exception("invalid http header line: " + line);
                }
                String name = line.Substring(0, separator);
                int pos = separator + 1;
                while ((pos < line.Length) && (line[pos] == ' '))
                {
                    pos++; // strip any spaces
                }

                string value = line.Substring(pos, line.Length - pos);
                Console.WriteLine("header: {0}:{1}", name, value);
                httpHeaders[name] = value;
            }
        }

        public void handleGETRequest()
        {
            srv.handleGETRequest(this);
        }

        private const int BUF_SIZE = 4096;
        public void handlePOSTRequest()
        {
            // this post data processing just reads everything into a memory stream.
            // this is fine for smallish things, but for large stuff we should really
            // hand an input stream to the request processor. However, the input stream 
            // we hand him needs to let him see the "end of the stream" at this content 
            // length, because otherwise he won't know when he's seen it all! 

            Console.WriteLine("get post data start");
            int content_len = 0;
            MemoryStream ms = new MemoryStream();
            if (this.httpHeaders.ContainsKey("Content-Length"))
            {
                content_len = Convert.ToInt32(this.httpHeaders["Content-Length"]);
                if (content_len > MAX_POST_SIZE)
                {
                    throw new Exception(
                        String.Format("POST Content-Length({0}) too big for this simple server",
                          content_len));
                }
                byte[] buf = new byte[BUF_SIZE];
                int to_read = content_len;
                while (to_read > 0)
                {
                    Console.WriteLine("starting Read, to_read={0}", to_read);

                    int numread = this.inputStream.Read(buf, 0, Math.Min(BUF_SIZE, to_read));
                    Console.WriteLine("read finished, numread={0}", numread);
                    if (numread == 0)
                    {
                        if (to_read == 0)
                        {
                            break;
                        }
                        else
                        {
                            throw new Exception("client disconnected during post");
                        }
                    }
                    to_read -= numread;
                    ms.Write(buf, 0, numread);
                }
                ms.Seek(0, SeekOrigin.Begin);
            }
            Console.WriteLine("get post data end");
            srv.handlePOSTRequest(this, new StreamReader(ms));

        }

        public void writeSuccess(string content_type = "text/html")
        {
            // this is the successful HTTP response line
            outputStream.WriteLine("HTTP/1.0 200 OK");
            // these are the HTTP headers...          
            outputStream.WriteLine("Content-Type: " + content_type);
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here if you like

            outputStream.WriteLine(""); // this terminates the HTTP headers.. everything after this is HTTP body..
        }

        public void writeFailure()
        {
            // this is an http 404 failure response
            outputStream.WriteLine("HTTP/1.0 404 File not found");
            // these are the HTTP headers
            outputStream.WriteLine("Connection: close");
            // ..add your own headers here

            outputStream.WriteLine(""); // this terminates the HTTP headers.
        }
    }

    public abstract class HttpServer
    {

        protected int port;
        protected IPAddress ip_addr;
        TcpListener listener;
        bool is_active = true;

        public HttpServer(String ip_addr, int port)
        {
            this.port = port;
            this.ip_addr = IPAddress.Parse(ip_addr);
        }

        public void listen()
        {
            listener = new TcpListener(ip_addr, port);
            listener.Start();
            while (is_active)
            {
                TcpClient s = listener.AcceptTcpClient();
                HttpProcessor processor = new HttpProcessor(s, this);
                Thread thread = new Thread(new ThreadStart(processor.process));
                thread.Start();
                Thread.Sleep(1);
            }
        }

        public abstract void handleGETRequest(HttpProcessor p);
        public abstract void handlePOSTRequest(HttpProcessor p, StreamReader inputData);
    }

    public class MyHttpServer : HttpServer
    {

        private Connector connector = null;
        private SMCStatus previousStatus = SMCStatus.None;

        void connector_ConnectorStatus(SMCStatus aStatus, object aConnector)
        {
            if (aStatus != previousStatus)
            {
                Console.WriteLine("Status: " + aStatus);
            }

            previousStatus = aStatus;
        }

        public MyHttpServer(String ip_addr, int port) : base(ip_addr, port)
        {
            if (!File.Exists("../../vendor/config.xml"))
            {
                Console.WriteLine("Fatal error: Configuration not found!");
                //TODO return 0;
            } else
            {
                Console.WriteLine("Found config.xml");
            }

            connector = ConnectorUtility.GetConnector();
            //TODO connector.ConnectorStatus += connector_ConnectorStatus;
        }
        public override void handleGETRequest(HttpProcessor p)
        {
            //XmlDocument doc = new XmlDocument();
            //XmlElement bookElement = doc.CreateElement("book", "http://www.contoso.com/books"); ///< book xmlns = "http://www.contoso.com/books" />
            //doc.AppendChild(bookElement);

           /* XmlTextWriter writer = new XmlTextWriter(p.outputStream);
            writer.Formatting = Formatting.Indented;
            doc.WriteTo(writer);
            writer.Flush();
            Console.WriteLine();*/
        
            Console.WriteLine("****** start SMCSettings ******");
            //Console.WriteLine(connector.SMCSettings);

            //De.Boenigk.SMC5D.Basics.Log log = new Log(true);

            XmlSerializer xsSubmit = new XmlSerializer(typeof(SMCSettings));
            XmlTextWriter writer = new XmlTextWriter(p.outputStream);
            writer.Formatting = Formatting.Indented;

            using (StringWriter sww = new StringWriter())
            using (XmlWriter w = XmlWriter.Create(sww))
            {
                xsSubmit.Serialize(w, ConnectorUtility.GetConnectorSettings());
                var xml = sww.ToString(); // Your XML
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                doc.WriteTo(writer);
                writer.Flush();
                Console.WriteLine();
            }

            Console.WriteLine("connector.IsSpindleOn: " + ConnectorUtility.GetConnector().IsSpindleOn());
            //Console.WriteLine("connector.IsUSB: " + ConnectorUtility.GetConnector().IsUSB);
            Console.WriteLine("Volt 1: " + ConnectorUtility.GetConnector().AD1Volt);
            Console.WriteLine("Volt 2: " + ConnectorUtility.GetConnector().AD2Volt);
            Console.WriteLine("GlobPos SpindleSpeed: " + ConnectorUtility.GetConnector().GlobPosition.SpindleSpeed);
            Console.WriteLine("GlobPos X: " + ConnectorUtility.GetConnector().GlobPosition.X);
            Console.WriteLine("GlobPos Y: " + ConnectorUtility.GetConnector().GlobPosition.Y);
            Console.WriteLine("GlobPos Z: " + ConnectorUtility.GetConnector().GlobPosition.Z);

            Console.WriteLine("****** end SMCSettings ******");


            /*
            Console.WriteLine("request: {0}", p.http_url);
            p.writeSuccess();
            p.outputStream.WriteLine("<html><body><h1>test server</h1>");
            p.outputStream.WriteLine("Current Time: " + DateTime.Now.ToString());
            p.outputStream.WriteLine("url : {0}", p.http_url);

            p.outputStream.WriteLine("<form method=post action=/form>");
            p.outputStream.WriteLine("<input type=text name=foo value=foovalue>");
            p.outputStream.WriteLine("<input type=submit name=bar value=barvalue>");
            p.outputStream.WriteLine("</form>");
            */
        }

        public override void handlePOSTRequest(HttpProcessor p, StreamReader inputData)
        {
            Console.WriteLine("POST request: {0}", p.http_url);
            string data = inputData.ReadToEnd();

            p.writeSuccess();
            p.outputStream.WriteLine("<html><body><h1>test server</h1>");
            p.outputStream.WriteLine("<a href=/test>return</a><p>");
            p.outputStream.WriteLine("postbody: <pre>{0}</pre>", data);


        }
    }


}