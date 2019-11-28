﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TSQL;
using TSQL.Tokens;

namespace MySqlServer
{
    public class ClientSession : IDisposable
    {
        #region Client Capability Flags
        // More capability flages in https://dev.mysql.com/doc/internals/en/capability-flags.html
        static uint CLIENT_FOUND_ROWS = 0x00000002;
        static uint CLIENT_CONNECT_WITH_DB = 0x00000008;
        static uint CLIENT_SECURE_CONNECTION = 0x00008000;
        static uint CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA = 0x00200000;
        static uint CLIENT_PLUGIN_AUTH = 0x00080000;
        static uint CLIENT_CONNECT_ATTRS = 0x00100000;
        static uint CLIENT_SSL = 0x00000800;
        static uint CLIENT_PROTOCOL_41 = 0x00000200;
        static uint CLIENT_SESSION_TRACK = 0x00800000;
        static uint CLIENT_DEPRECATE_EOF = 0x01000000;
        #endregion

        #region Server Status Flags
        // More server flags in https://dev.mysql.com/doc/internals/en/status-flags.html
        static uint SERVER_MORE_RESULTS_EXISTS = 0x0008;
        #endregion

        #region Column type
        // More types in https://dev.mysql.com/doc/internals/en/com-query-response.html#column-type
        public enum ColumnType
        {
            MYSQL_TYPE_LONG = 0x03,
            MYSQL_TYPE_FLOAT = 0x04,
            MYSQL_TYPE_DOUBLE = 0x05,
            MYSQL_TYPE_NULL = 0x06,
            MYSQL_TYPE_TIMESTAMP = 0x07,
            MYSQL_TYPE_LONGLONG = 0x08,
            MYSQL_TYPE_INT24 = 0x09,

            MYSQL_TYPE_TIME = 0x0b,

            MYSQL_TYPE_VARCHAR = 0x0f,
            MYSQL_TYPE_VAR_STRING = 0xfd,
            MYSQL_TYPE_STRING = 0xfe
        }
        #endregion

        #region Public-Members

        public TcpClient TcpClient
        {
            get { return _TcpClient; }
        }

        public NetworkStream NetworkStream
        {
            get { return _NetworkStream; }
        }

        public SslStream SslStream
        {
            get { return _SslStream; }
            set { _SslStream = value; }
        }

        public string IpPort
        {
            get { return _IpPort; }
        }

        public string ConnectedDatabase
        {
            get { return _ConnectedDB; }
            set { _ConnectedDB = value; }
        }

        public CancellationTokenSource TokenSource { get; set; }

        public CancellationToken Token { get; set; }

        #endregion

        #region Private-Members

        private Server _Server;

        // ssl related members
        private X509Certificate2 _SslCertificate = null;
        private X509Certificate2Collection _SslCertificateCollection = null;

        // tcp connection related members
        private int _ReceiveBufferSize = 4096;
        private TcpClient _TcpClient = null;
        private NetworkStream _NetworkStream = null;
        private SslStream _SslStream = null;
        private string _IpPort = null;
        public bool _UseSsl = false;

        // mysql server related members
        private uint _ClientCapabilities;
        private string _ConnectedDB;
        private DatabaseController _DatabaseController;
        public enum Phase { Waiting, ConnectionPhase, CommandPhase };
        public Phase _ServerPhase = Phase.Waiting;
        private int _Sequence = 0;
        private byte[] _Salt1; // 8 bytes
        private byte[] _Salt2; // 12 bytes

        public bool Debug = false;

        #endregion

        #region Constructors-and-Factories

        public ClientSession(TcpClient tcp, Server server, DatabaseController db)
        {
            if (tcp == null) throw new ArgumentNullException(nameof(tcp));

            _Server = server;

            _TcpClient = tcp;
            _NetworkStream = tcp.GetStream();
            _IpPort = tcp.Client.RemoteEndPoint.ToString();

            _DatabaseController = db;
            _ServerPhase = Phase.Waiting;
        }
        #endregion

        #region Public-Methods

        public void ClientConnected()
        {
            LogBasic("start handshake");
            SetState(Phase.ConnectionPhase);
            HandleHandshake();

            byte[] buffer = new byte[_ReceiveBufferSize];
            _NetworkStream.Read(buffer);
            HandleHandshakeResponse(buffer);

            if (_UseSsl)
            {
                Log("use ssl");
                _SslCertificate = new X509Certificate2(_Server.CertFilename, _Server.CertPassword);

                _SslCertificateCollection = new X509Certificate2Collection { _SslCertificate };

                if (_Server.AcceptInvalidCertificates)
                {
                    _SslStream = new SslStream(_NetworkStream, false, new RemoteCertificateValidationCallback(AcceptCertificate));
                }
                else
                {
                    _SslStream = new SslStream(_NetworkStream, false);
                }

                bool success = StartTls();

                if (!success)
                {
                    Dispose();
                }
                else
                {
                    Log("start tls success");
                }
            }
        }

        public void DataReceived(byte[] data)
        {
            if (_ServerPhase == Phase.ConnectionPhase)
            {
                Log("handshake after start tls");
                HandleHandshakeResponse(data);
            }
            else if (_ServerPhase == Phase.CommandPhase)
            {
                _Sequence = 0;
                HandleCommand(data);
            }
        }

        public void ClientDisconnected()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_SslStream != null)
            {
                _SslStream.Close();
                _SslStream.Dispose();
                _SslStream = null;
            }

            if (_NetworkStream != null)
            {
                _NetworkStream.Close();
                _NetworkStream.Dispose();
                _NetworkStream = null;
            }

            if (_TcpClient != null)
            {
                _TcpClient.Close();
                _TcpClient.Dispose();
                _TcpClient = null;
            }
        }

        #endregion

        #region Private-Methods

        private bool StartTls()
        {
            try
            {
                _SslStream.AuthenticateAsServer(
                    _SslCertificate,
                    _Server.MutuallyAuthenticate,
                    SslProtocols.Tls12,
                    !_Server.AcceptInvalidCertificates);
                if (!_SslStream.IsEncrypted)
                {
                    Log("[" + IpPort + "] not encrypted");
                    Dispose();
                    return false;
                }

                if (!_SslStream.IsAuthenticated)
                {
                    Log("[" + IpPort + "] stream not authenticated");
                    Dispose();
                    return false;
                }

                if (_Server.MutuallyAuthenticate && !_SslStream.IsMutuallyAuthenticated)
                {
                    Log("[" + IpPort + "] failed mutual authentication");
                    Dispose();
                    return false;
                }
            }
            catch (Exception e)
            {
                Log("[" + IpPort + "] TLS exception" + Environment.NewLine + e.ToString());
                Dispose();
                return false;
            }
            Log("ssl stream ok");
            return true;
        }

        private bool AcceptCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            // return true; // Allow untrusted certificates.
            return _Server.AcceptInvalidCertificates;
        }

        private void DisconnectClient()
        {
            LogBasic("disconnect");
            Dispose();
        }

        // State machine
        private void SetState(Phase phase)
        {
            // exit state
            switch (_ServerPhase)
            {
                // exit command phase
                case Phase.CommandPhase:
                    break;
            }

            _ServerPhase = phase;

            // enter state
        }

        /// <summary>
        /// Send initial handshake packet
        /// </summary>
        private void HandleHandshake()
        {
            List<byte> packet = new List<byte>();

            // 1 byte protocol version
            packet.Add(0x0a);
            // server version
            byte[] server_version = NulTerminatedString("5.7.28");
            packet.AddRange(server_version);
            // 4 bytes connection id
            byte[] connection_id = BitConverter.GetBytes(30);
            packet.AddRange(connection_id); // thread id

            // Salt1
            // first 8 bytes of the auth-plugin data
            // last byte 0x00 as filler
            GenerateSalt1();
            packet.AddRange(_Salt1);
            packet.Add(0x00);

            //Server Capabilities: 0xffff
            packet.Add(0xff);
            packet.Add(0xff);

            // Server Language: Unknown (255)
            packet.Add(0xff);

            // Server Status: 0x0200
            packet.Add(0x02);
            packet.Add(0x00);

            // Extended Server Capabilities: 0xffx7
            packet.Add(0xff);
            packet.Add(0xc7);

            // Authentication Plugin Length: 21
            packet.Add(0x15);

            // Unused: 00000000000000000000
            for (var i = 0; i < 10; i++)
            {
                packet.Add(0x00);
            }

            // Salt2
            GenerateSalt2();
            packet.AddRange(_Salt2);
            packet.Add(0x00);

            // Authentication Plugin: mysql_native_password
            packet.AddRange(NulTerminatedString("mysql_native_password"));

            SendPacket(packet.ToArray());
            Log("send initial handshake");
        }

        /// <summary>
        /// Handle handshake response packet
        /// </summary>
        /// <param name="data">response data</param>
        private void HandleHandshakeResponse(byte[] data)
        {
            Log("handle handshake response");
            GetSequence();

            // get mysql packet length
            // first 3 bytes is packet length, fixed length integer
            byte[] packetLengthBytes = new byte[3];
            Array.Copy(data, packetLengthBytes, 3);
            int packetLengthInt = FixedLengthInteger_toInt(packetLengthBytes);
            Log("packetLength: " + packetLengthInt);

            byte sequence = data[3];
            //Log("sequence: {0}", (int)sequence);

            // get login request package
            byte[] loginRequestBytes = SubArray(data, 4, packetLengthInt);
            long currentHead = 0;

            // capability flags
            byte[] clientCapabilitiesBytes = SubArray(loginRequestBytes, currentHead, 4); // client capabilities
            currentHead += 4;
            uint clientCapabilities = BitConverter.ToUInt32(clientCapabilitiesBytes, 0);
            _ClientCapabilities = clientCapabilities;

            // max-packet size
            byte[] maxPacketSize = SubArray(loginRequestBytes, currentHead, 4);
            currentHead += 4;

            // character set
            byte[] characterSet = SubArray(loginRequestBytes, currentHead, 1);
            currentHead += 1;

            // reserved unused
            currentHead += 23;

            // Switch to SSL exchange
            if (Convert.ToBoolean(clientCapabilities & CLIENT_SSL) && !_UseSsl)
            {
                _UseSsl = true;
                return;
            }


            // username, string nul type
            byte[] usernameBytes = SubArray(loginRequestBytes, currentHead, packetLengthInt - currentHead);
            string usernameStr = NulTerminatedString_bytesToString(usernameBytes);
            currentHead += NulTerminatedString_stringLength(usernameBytes);
            Log("username: " + usernameStr);

            byte[] correctPassword = Encoding.ASCII.GetBytes(_DatabaseController.GetUserPassword(usernameStr));
            bool passedVerification = false;

            // auth-response
            // check capability flags
            if (Convert.ToBoolean(clientCapabilities & CLIENT_PLUGIN_AUTH_LENENC_CLIENT_DATA))
            {
                // lenenc-int password length
                byte[] passwordLengthBytes = SubArray(loginRequestBytes, currentHead, 8);
                long passwordLength = LengthEncodedInteger_toInt(passwordLengthBytes);
                currentHead += LengthEncodedInteger_intLength(passwordLengthBytes);

                byte[] passwordBytes = SubArray(loginRequestBytes, currentHead, passwordLength);
                currentHead += passwordLength;
                Log("password length: {"+ passwordLength + "}, {"+ ByteArrayToHexString(passwordBytes) + "}");

                passedVerification = VerifyPassword(passwordBytes, correctPassword);
            }
            else if (Convert.ToBoolean(clientCapabilities & CLIENT_SECURE_CONNECTION))
            {
                // 1 byte password length
                int passwordLength = Convert.ToInt32(loginRequestBytes[currentHead]);
                currentHead += 1;

                byte[] passwordBytes = SubArray(loginRequestBytes, currentHead, passwordLength);
                currentHead += passwordLength;
                Log("password length: {" + passwordLength + "}, {" + ByteArrayToHexString(passwordBytes) + "}");

                passedVerification = VerifyPassword(passwordBytes, correctPassword);
            }
            else
            {
                // string nul type password
                byte[] passwordBytes = SubArray(loginRequestBytes, currentHead, packetLengthInt - currentHead);
                int passwordLength = NulTerminatedString_stringLength(passwordBytes);
                byte[] password = SubArray(loginRequestBytes, currentHead, passwordLength);
                currentHead += passwordLength;
                Log("password length: {" + passwordLength + "}, {" + ByteArrayToHexString(passwordBytes) + "}");

                passedVerification = VerifyPassword(passwordBytes, correctPassword);
            }

            // If have database name input
            if (Convert.ToBoolean(clientCapabilities & CLIENT_CONNECT_WITH_DB))
            {
                // string nul type
                byte[] databaseNameBytes = SubArray(loginRequestBytes, currentHead, packetLengthInt - currentHead);
                string databaseName = NulTerminatedString_bytesToString(databaseNameBytes);
                currentHead += NulTerminatedString_stringLength(databaseNameBytes);
                Log("database string bytes length {"+ NulTerminatedString_stringLength(databaseNameBytes) + "} name: {"+ databaseName + "}");
                // TODO: set connected database name, handle error if database not exist
                _ConnectedDB = databaseName;
            }

            // If has auth plugin name
            if (Convert.ToBoolean(clientCapabilities & CLIENT_PLUGIN_AUTH))
            {
                // string nul type
                byte[] authNameBytes = SubArray(loginRequestBytes, currentHead, packetLengthInt - currentHead);
                string authPluginName = NulTerminatedString_bytesToString(authNameBytes);
                currentHead += NulTerminatedString_stringLength(authNameBytes);
                Log("auth plugin string bytes length {"+ NulTerminatedString_stringLength(authNameBytes) + "} name: {"+ authPluginName + "}");
            }

            // If have client connect attributs
            if (Convert.ToBoolean(clientCapabilities & CLIENT_CONNECT_ATTRS))
            {
                // TODO:lenenc int type, length of all key-values
            }

            if (passedVerification)
            {
                SendOkPacket();
                SetState(Phase.CommandPhase);
            }
            else
            {
                // fail login
                SendErrPacket("wrong password");
                // Disconnect
                DisconnectClient();
                SetState(Phase.Waiting);
            }

        }

        private void HandleCommand(byte[] data)
        {

            // Get packet length and sequence number
            byte[] packetLength = new byte[3];
            byte sequence = data[3];
            Array.Copy(data, packetLength, 3);
            int packetLengthInt = packetLength[0];
            Log("sql packet length: "+ packetLengthInt);

            // Get text protocol
            byte[] queryPacket = SubArray(data, 4, packetLengthInt);
            byte textProtocol = queryPacket[0];

            if (textProtocol == 0x03) // COM_QUERY
            {
                Log("get COM_QUERY");
                GetSequence();
                byte[] queryBytes = SubArray(queryPacket, 1, packetLengthInt - 1);
                string queryString = Encoding.ASCII.GetString(queryBytes, 0, queryBytes.Length);
                HandleQuery(queryString);
                return;
            }

            if (textProtocol == 0x01) // COM_QUIT
            {
                Log("COM_QUIT disconnect");
                SetState(Phase.Waiting);
                DisconnectClient();
                return;
            }

            if (textProtocol == 0x0e)
            {
                Log("COM_PING ping");
                SendOkPacket();
            }

            Log("other command");
        }

        private void HandleQuery(string query)
        {
            LogBasic("handle query: {"+ query + "}");
            List<TSQLToken> tokens = TSQLTokenizer.ParseTokens(query);
            foreach (var token in tokens)
            {
                Log("type: " + token.Type.ToString() + ", value: " + token.Text);
            }

            Table returnTable = _DatabaseController.GetDatabase("dummy").GetTable("dummy");

            if (tokens[0].Text.ToLower() == "select")
            {
                returnTable = _DatabaseController.Select(tokens);
            }
            else if (tokens[0].Text.ToLower() == "show")
            {
                returnTable = _DatabaseController.Show(tokens);
            }
            else if (tokens[0].Text.ToLower() == "set")
            {
                _DatabaseController.Set(tokens);
                SendOkPacket();
                return;
            }

            Column[] h = returnTable.Columns;
            Row[] r = returnTable.Rows;


            //Log("Query results:");
            //Log("Cols:");
            //foreach (var col in h)
            //{
            //    Log("\tname: {"+ col.ColumnName + "}, type: {"+ col._ColumnType + "}");
            //}
            //Log("Rows:");
            //foreach (var row in r)
            //{
            //    foreach (var v in row._Values)
            //    {
            //        Log("\tvalue: " + v);
            //    }
            //}

            // send length encoded packet
            SendPacket(LengthEncodedInteger(h.Length));

            // send column definition packet
            SendColumnDefinition(h);

            // send eof packet
            // If the CLIENT_DEPRECATE_EOF client capability flag is set, OK_Packet is sent; else EOF_Packet is sent.
            // ?? which seems not correct. more info in https://dev.mysql.com/doc/internals/en/capability-flags.html
            if (Convert.ToBoolean(_ClientCapabilities & CLIENT_DEPRECATE_EOF))
            {
                //SendOkPacket();
            }
            else
            {
                SendEofPacket();
            }

            // send row packet
            SendTextResultsetRow(r);

            // send eof packet
            SendEofPacket();
            return;
        }

        private void SendColumnDefinition(Column[] columns)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                Column column = columns[i];
                List<byte> packet = new List<byte>();

                int character_set = 33; // utf8_general_ci
                int max_col_length = 1024; //This is totally made up.  it shouldn't be
                byte column_type = (byte)column._ColumnType;

                packet.AddRange(LengthEncodedString("def")); // catalog
                packet.AddRange(LengthEncodedString(column.ColumnName)); // schema-name
                packet.AddRange(LengthEncodedString(column.TableName)); // virtual
                packet.AddRange(LengthEncodedString(column.TableName)); // physical table-name
                packet.AddRange(LengthEncodedString(column.ColumnName)); // virtual column name
                packet.AddRange(LengthEncodedString(column.ColumnName)); // physical column name
                packet.Add(0x0c); // length of the following fields (always 0x0c)
                packet.AddRange(FixedLengthInteger(character_set, 2)); // character_set is the column character set and is defined in Protocol::CharacterSet.
                packet.AddRange(FixedLengthInteger(max_col_length, 4)); // maximum length of the field
                packet.AddRange(FixedLengthInteger(column_type, 1)); // column_type, type of the column as defined in Column Type

                // flags
                packet.Add(0x00);
                packet.Add(0x00);

                // decimals, max shown decimal digits
                packet.Add(0x00); // 0x00 for integers and static strings
                                  // 0x1f for dynamic strings, double, float
                                  // 0x00 to 0x51 for decimals

                packet.Add(0x00); //Filler
                packet.Add(0x00);

                SendPacket(packet.ToArray());
            }
        }


        private void SendTextResultsetRow(Row[] rows)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                Row row = rows[i];
                List<byte> packet = new List<byte>();

                for (var j = 0; j < row._Values.Length; j++)
                {
                    packet.AddRange(LengthEncodedString(row._Values[j].ToString()));
                }
                SendPacket(packet.ToArray());
            }
        }

        private int GetSequence()
        {
            int val = _Sequence;
            _Sequence += 1;
            if (_Sequence > 255)
            {
                _Sequence = 0;
            }
            return val;
        }

        private void GenerateSalt1()
        {
            _Salt1 = GetSalt(8);
        }

        private void GenerateSalt2()
        {
            _Salt2 = GetSalt(12);
        }

        private static byte[] GetSalt(int maximumSaltLength)
        {
            var salt = new byte[maximumSaltLength];
            using (var random = new RNGCryptoServiceProvider())
            {
                random.GetNonZeroBytes(salt);
            }

            return salt;
        }


        /// <summary>
        /// Verify 20-byte-password with real password
        /// </summary>
        /// <param name="inputPassword">20-byte-long input password</param>
        /// <param name="userPassword">user password</param>
        /// <returns></returns>
        private bool VerifyPassword(byte[] inputPassword, byte[] userPassword)
        {
            if (inputPassword.Length != 20)
            {
                return false;
            }
            // password is calculated by
            // SHA1( password ) XOR SHA1( "20-bytes random data from server" <concat> SHA1( SHA1( password ) ) )
            byte[] salt = ConcatArrays(_Salt1, _Salt2);
            SHA1 sha1Hash = SHA1.Create();

            // part 1
            byte[] part1 = sha1Hash.ComputeHash(userPassword);

            // part 2
            byte[] partBytes = ConcatArrays(salt, sha1Hash.ComputeHash(part1));
            byte[] part2 = sha1Hash.ComputeHash(partBytes);

            // XOR
            byte[] result = new byte[20];
            for (var i = 0; i < 20; i++)
            {
                byte b = (byte)(part1[i] ^ part2[i]);
                if (b != inputPassword[i])
                {
                    Log("wrong password");
                    return false;
                }
                result[i] = b;
            }

            Log("correct password");
            //Log("correct {0}, come in {1}", ByteArrayToHexString(result), ByteArrayToHexString(inputPassword));

            return true;
        }

        #region Generic Response Packets

        private void SendOkPacket()
        {
            List<byte> ok = new List<byte>();
            ok.Add(0x00); // OK
            ok.Add(0x00); // affected rows
            ok.Add(0x00); // last insert id
            ok.Add(0x02); // Say autocommit was set
            ok.Add(0x00);
            ok.Add(0x00); // No warnings
            ok.Add(0x00);

            SendPacket(ok.ToArray());
            Log("send ok packet");
        }

        private void SendErrPacket(String message)
        {
            List<byte> bytes = new List<byte>();

            bytes.Add(0xff); //[ff] header of the ERR packet

            byte[] errorCode = { 0x15, 0x04 };
            bytes.AddRange(errorCode);

            if (Convert.ToBoolean(_ClientCapabilities & CLIENT_PROTOCOL_41))
            {
                // sql_state_marker, string[1]
                bytes.Add(0x23);

                // sql_state, string[5]
                bytes.AddRange(FixedLengthString("28000", 5));
            }

            bytes.AddRange(RestOfPacketString(message));

            SendPacket(bytes.ToArray());
            Log("send err packet");
        }

        private void SendEofPacket()
        {
            List<byte> packet = new List<byte>();

            // [fe] EOF header
            packet.Add(0xfe);

            if (Convert.ToBoolean(_ClientCapabilities & CLIENT_PROTOCOL_41))
            {
                // number of warnings
                packet.Add(0x00); // warnings
                packet.Add(0x00);

                // Status Flags
                packet.Add(0x22);
                packet.Add(0x00);
            }

            SendPacket(packet.ToArray());
        }

        private void SendPacket(byte[] data)
        {
            List<byte> packet = new List<byte>();
            byte[] length = FixedLengthInteger(data.Length, 3);
            byte[] seq = FixedLengthInteger(GetSequence(), 1);
            packet.AddRange(length);
            packet.AddRange(seq);
            packet.AddRange(data);

            byte[] packetArray = packet.ToArray();

            if (!_UseSsl)
            {
                _NetworkStream.Write(packetArray, 0, packetArray.Length);
                _NetworkStream.Flush();
            }
            else
            {
                _SslStream.Write(packetArray, 0, packetArray.Length);
                _SslStream.Flush();
            }
        }

        #endregion

        #endregion

        #region Integer types

        // type int<>
        public static byte[] FixedLengthInteger(int theInt, int length)
        {
            byte[] resultArray = new byte[length];
            for (int i = length - 1; i >= 0; i--)
            {
                resultArray[i] = (byte)(theInt >> (i * 8));
            }
            return resultArray;
        }

        public static int FixedLengthInteger_toInt(byte[] bytes)
        {
            int sum = 0;
            for (var i = bytes.Length - 1; i >= 0; i--)
            {
                sum += (int)(bytes[i] << (8 * i));
            }
            return sum;
        }

        // type int<lenenc>
        public static byte[] LengthEncodedInteger(long value)
        {
            if (value >= 251 && value < Math.Pow(2, 16))
            {
                // 0xfc + 2-byte integer
                return new byte[] { 0xfc, (byte)value, (byte)(value >> 8) };
            }
            if (value >= Math.Pow(2, 16) && value < Math.Pow(2, 24))
            {
                // 0xfd + 3-byte integer
                return new byte[] { 0xfd, (byte)value, (byte)(value >> 8), (byte)(value >> 16) };
            }
            if (value >= Math.Pow(2, 24) && value < Math.Pow(2, 64))
            {
                return new byte[] { 0xfe,
                    (byte)value,
                    (byte)(value >> 8),
                    (byte)(value >> 16),
                    (byte)(value >> 24),
                    (byte)(value >> 32),
                    (byte)(value >> 40),
                    (byte)(value >> 48),
                    (byte)(value >> 56),
                    (byte)(value >> 64)
                }; // 0xfe + 8-byte integer
            }

            // stored as a 1-byte integer
            return new byte[] { (byte)value };
        }

        public static long LengthEncodedInteger_toInt(byte[] bytes)
        {
            // 8-byte integer
            if (bytes[0] == 0xfe)
            {
                //Console.WriteLine("8-byte int");
                return FixedLengthInteger_toInt(SubArray(bytes, 1, 8));
            }
            // 3-byte integer
            if (bytes[0] == 0xfd)
            {
                //Console.WriteLine("3-byte int");
                return FixedLengthInteger_toInt(SubArray(bytes, 1, 3));
            }
            // 2-byte integer
            if (bytes[0] == 0xfc)
            {
                //Console.WriteLine("2-byte int");
                return FixedLengthInteger_toInt(SubArray(bytes, 1, 2));
            }

            // 1-byte integer
            //Console.WriteLine("1-byte int");
            return bytes[0];
        }

        public static int LengthEncodedInteger_intLength(byte[] bytes)
        {
            // 8-byte integer
            if (bytes[0] == 0xfe)
            {
                //Console.WriteLine("8-byte int");
                return 8;
            }
            // 3-byte integer
            if (bytes[0] == 0xfd)
            {
                //Console.WriteLine("3-byte int");
                return 3;
            }
            // 2-byte integer
            if (bytes[0] == 0xfc)
            {
                //Console.WriteLine("2-byte int");
                return 2;
            }

            // 1-byte integer
            //Console.WriteLine("1-byte int");
            return 1;
        }

        #endregion

        #region string types

        // type string<lenenc>
        public static byte[] LengthEncodedString(string str)
        {
            List<byte> bytes = new List<byte>();
            bytes.AddRange(LengthEncodedInteger(str.Length));
            byte[] stringByte = Encoding.ASCII.GetBytes(str);
            bytes.AddRange(stringByte);
            return bytes.ToArray();
        }

        // type string<fix>
        public static byte[] FixedLengthString(string str, int length)
        {
            byte[] bytes = new byte[length];

            try
            {
                Array.Copy(Encoding.ASCII.GetBytes(str), bytes, str.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine("string length longer than fix length {0}", ex);
            }
            return bytes;
        }

        // tyoe string<var>
        // The length of the string is determined by another field or is calculated at runtimes
        public static byte[] VariableLengthString(string str, int length)
        {
            byte[] bytes = new byte[length];
            Array.Copy(Encoding.ASCII.GetBytes(str), bytes, Math.Min(20, str.Length));
            return bytes;
        }

        // type string<EOF>
        public static byte[] RestOfPacketString(string str)
        {
            return Encoding.ASCII.GetBytes(str);
        }

        // type string<NUL>, Strings that are terminated by a [00] byte.
        public static byte[] NulTerminatedString(string str)
        {
            List<byte> bytes = new List<byte>();
            byte[] stringByte = Encoding.ASCII.GetBytes(str);
            bytes.AddRange(stringByte);
            bytes.Add(0x00);

            return bytes.ToArray();
        }


        /// <summary>
        /// Convert string<NUL> bytes to sting
        /// </summary>
        /// <param name="bytes">bytes of encoded string nul</param>
        /// <returns>decoded string</returns>
        public static string NulTerminatedString_bytesToString(byte[] bytes)
        {
            List<byte> stringBytes = new List<byte>();
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x00)
                {
                    break;
                }
                stringBytes.Add(bytes[i]);
            }

            return Encoding.ASCII.GetString(stringBytes.ToArray());
        }

        /// <summary>
        /// Get length of string nul
        /// </summary>
        /// <param name="bytes">bytes of encoded string nul</param>
        /// <returns>length of string bytes, include [00] byte</returns>
        public static int NulTerminatedString_stringLength(byte[] bytes)
        {
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0x00)
                {
                    return i + 1; // add one to include [00] byte
                }
            }
            return 0;
        }

        #endregion


        #region Helper-Methods

        /// <summary>
        /// Sub array
        /// </summary>
        /// <param name="bytes">big array</param>
        /// <param name="index">start index</param>
        /// <param name="length">length of new array</param>
        /// <returns>subarray</returns>
        public static T[] SubArray<T>(T[] bytes, long index, long length)
        {
            T[] result = new T[length];
            try
            {
                Array.Copy(bytes, index, result, 0, length);
            }
            catch (Exception ex)
            {
                Console.WriteLine("error in subarray");
                throw new Exception("error in subarray");
            }
            return result;
        }

        /// <summary>
        /// Combine arrays to one array
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="list"></param>
        /// <returns></returns>
        public static T[] ConcatArrays<T>(params T[][] list)
        {
            var result = new T[list.Sum(a => a.Length)];
            int offset = 0;
            for (int x = 0; x < list.Length; x++)
            {
                list[x].CopyTo(result, offset);
                offset += list[x].Length;
            }
            return result;
        }

        /// <summary>
        /// Convert byte array to hex strings for display;
        /// </summary>
        /// <param name="ba"></param>
        /// <returns></returns>
        public static string ByteArrayToHexString(byte[] ba)
        {
            StringBuilder hex = new StringBuilder(ba.Length * 2);
            foreach (byte b in ba)
                hex.AppendFormat("{0:x2}", b);
            return hex.ToString();
        }

        public byte[] GetBytesFromPEM(string pemString, string section)
        {
            var header = String.Format("-----BEGIN {0}-----", section);
            var footer = String.Format("-----END {0}-----", section);

            var start = pemString.IndexOf(header, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += header.Length;
            var end = pemString.IndexOf(footer, start, StringComparison.Ordinal) - start;

            if (end < 0)
                return null;

            return Convert.FromBase64String(pemString.Substring(start, end));
        }

        public string GetStringFromPEM(string pemString, string section)
        {
            var header = String.Format("-----BEGIN {0}-----", section);
            var footer = String.Format("-----END {0}-----", section);

            var start = pemString.IndexOf(header, StringComparison.Ordinal);
            if (start < 0)
                return null;

            start += header.Length;
            var end = pemString.IndexOf(footer, start, StringComparison.Ordinal) - start;

            if (end < 0)
                return null;

            return pemString.Substring(start, end);
        }

        public static void PrintAllBytes(byte[] data)
        {
            foreach (byte b in data)
            {
                Console.WriteLine(Convert.ToString(b, 2).PadLeft(8, '0'));
            }
        }

        public void LogBasic(string msg)
        {
            string timeStr = DateTime.Now.Minute.ToString() + '.' + DateTime.Now.Second.ToString() + '.' + DateTime.Now.Millisecond.ToString();
            Console.WriteLine("[client metadata][" + timeStr + "]" + "[" + _IpPort + "] " + msg);
        }

        public void Log(string msg)
        {
            if (Debug)
            {
                string timeStr = DateTime.Now.Minute.ToString() + '.' + DateTime.Now.Second.ToString() + '.' + DateTime.Now.Millisecond.ToString();
                Console.WriteLine("[client metadata][" + timeStr + "]" + "[" + _IpPort + "] " + msg);
            }
        }

        #endregion
    }
}