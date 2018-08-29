﻿using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;

namespace Multiplayer.Common
{
    public class MultiplayerServer
    {
        static MultiplayerServer()
        {
            MultiplayerConnectionState.RegisterState(typeof(ServerWorldState));
            MultiplayerConnectionState.RegisterState(typeof(ServerPlayingState));
        }

        public static MultiplayerServer instance;

        public const int DefaultPort = 30502;

        public byte[] savedGame; // Compressed game save
        public Dictionary<int, int> mapTiles = new Dictionary<int, int>(); // World tile to map id
        public Dictionary<int, byte[]> mapData = new Dictionary<int, byte[]>(); // Map id to compressed map data
        public Dictionary<int, List<byte[]>> mapCmds = new Dictionary<int, List<byte[]>>(); // Map id to serialized cmds list
        public List<byte[]> globalCmds = new List<byte[]>(); // Serialized global cmds
        public Dictionary<string, int> playerFactions = new Dictionary<string, int>(); // Username to faction id

        public List<ServerPlayer> players = new List<ServerPlayer>();

        public int timer;
        public ActionQueue queue = new ActionQueue();
        public string host;
        public string saveFolder;
        public string worldId;
        public IPAddress addr;
        public int port;
        public volatile bool running = true;

        private NetManager server;

        public int nextUniqueId;

        public MultiplayerServer(IPAddress addr, int port = DefaultPort)
        {
            this.addr = addr;
            this.port = port;

            EventBasedNetListener listener = new EventBasedNetListener();
            server = new NetManager(listener, 32, "");

            listener.PeerConnectedEvent += peer => Enqueue(() => PeerConnected(peer));
            listener.PeerDisconnectedEvent += (peer, info) => Enqueue(() => PeerDisconnected(peer, info));
            listener.NetworkLatencyUpdateEvent += (peer, latency) => Enqueue(() => UpdateLatency(peer, latency));

            listener.NetworkReceiveEvent += (peer, reader) =>
            {
                byte[] data = reader.Data;
                Enqueue(() => MessageReceived(peer, data));
            };
        }

        public void Run()
        {
            while (running)
            {
                server.PollEvents();

                queue.RunQueue();

                if (timer % 3 == 0)
                    SendToAll(Packets.SERVER_TIME_CONTROL, new object[] { timer });

                timer += 3;

                if (timer % 180 == 0)
                    UpdatePlayerList();

                Thread.Sleep(50); // 3 game ticks
            }

            server.Stop();
        }

        public void StartListening()
        {
            server.Start(port);
        }

        public void DoAutosave()
        {
            Enqueue(() =>
            {
                SendCommand(CommandType.AUTOSAVE, ScheduledCommand.NoFaction, ScheduledCommand.Global, new byte[0]);

                globalCmds.Clear();
                foreach (int mapId in mapCmds.Keys)
                    mapCmds[mapId].Clear();
            });
        }

        public void UpdatePlayerList()
        {
            string[] playerList = players.Select(player => $"{player.Username} ({player.Latency})").ToArray();
            SendToAll(Packets.SERVER_PLAYER_LIST, new object[] { playerList });
        }

        public void Enqueue(Action action)
        {
            queue.Enqueue(action);
        }

        public void PeerConnected(NetPeer peer)
        {
            IConnection conn = new MultiplayerConnection(peer);
            peer.Tag = conn;
            conn.State = new ServerWorldState(conn);
            players.Add(new ServerPlayer(conn));
        }

        public void PeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            IConnection conn = peer.GetConnection();
            ServerPlayer player = players.Find(p => p.connection == conn);

            players.Remove(player);

            if (!players.Any(p => p.FactionId == player.FactionId))
            {
                byte[] data = ByteWriter.GetBytes(player.FactionId);
                SendCommand(CommandType.FACTION_OFFLINE, ScheduledCommand.NoFaction, ScheduledCommand.Global, data);
            }

            SendToAll(Packets.SERVER_NOTIFICATION, new object[] { "Player " + conn.Username + " disconnected." });
            UpdatePlayerList();
        }

        public void MessageReceived(NetPeer peer, byte[] data)
        {
            IConnection conn = peer.GetConnection();
            conn.HandleReceive(data);
        }

        public void UpdateLatency(NetPeer peer, int latency)
        {
            IConnection conn = peer.GetConnection();
            conn.Latency = latency;
        }

        public void SendToAll(Enum id, byte[] data)
        {
            foreach (ServerPlayer player in players)
                player.connection.Send(id, data);
        }

        public void SendToAll(Enum id, object[] data)
        {
            SendToAll(id, ByteWriter.GetBytes(data));
        }

        public ServerPlayer GetPlayer(string username)
        {
            return players.FirstOrDefault(player => player.Username == username);
        }

        public IdBlock NextIdBlock()
        {
            int blockSize = 30000;
            int blockStart = nextUniqueId;
            nextUniqueId = nextUniqueId + blockSize;
            MpLog.Log("New id block " + blockStart + " of size " + blockSize);

            return new IdBlock(blockStart, blockSize);
        }

        public void SendCommand(CommandType cmd, int factionId, int mapId, byte[] extra)
        {
            // todo cull target players if not global
            byte[] toSend = ByteWriter.GetBytes(cmd, timer, factionId, mapId, extra);

            if (mapId < 0)
                globalCmds.Add(toSend);
            else
                mapCmds.AddOrGet(mapId, new List<byte[]>()).Add(toSend);

            SendToAll(Packets.SERVER_COMMAND, toSend);
        }
    }

    public class ServerPlayer
    {
        public IConnection connection;

        public string Username => connection.Username;
        public int Latency => connection.Latency;
        public int FactionId => MultiplayerServer.instance.playerFactions[Username];

        public ServerPlayer(IConnection connection)
        {
            this.connection = connection;
        }
    }

    public class IdBlock
    {
        public int blockStart;
        public int blockSize;
        public int mapId = -1;

        public int current;
        public bool overflowHandled;

        public IdBlock(int blockStart, int blockSize, int mapId = -1)
        {
            this.blockStart = blockStart;
            this.blockSize = blockSize;
            this.mapId = mapId;
        }

        public int NextId()
        {
            // Overflows should be handled by the caller
            current++;
            return blockStart + current;
        }

        public byte[] Serialize()
        {
            return ByteWriter.GetBytes(blockStart, blockSize, mapId, current);
        }

        public static IdBlock Deserialize(ByteReader data)
        {
            IdBlock block = new IdBlock(data.ReadInt32(), data.ReadInt32(), data.ReadInt32());
            block.current = data.ReadInt32();
            return block;
        }
    }

    public class ActionQueue
    {
        private Queue<Action> queue = new Queue<Action>();
        private Queue<Action> tempQueue = new Queue<Action>();

        public void RunQueue()
        {
            lock (queue)
            {
                if (queue.Count > 0)
                {
                    foreach (Action a in queue)
                        tempQueue.Enqueue(a);
                    queue.Clear();
                }
            }

            try
            {
                while (tempQueue.Count > 0)
                    tempQueue.Dequeue().Invoke();
            }
            catch (Exception e)
            {
                MpLog.LogLines("Exception while executing action queue", e.ToString());
            }
        }

        public void Enqueue(Action action)
        {
            lock (queue)
                queue.Enqueue(action);
        }
    }

    public class PacketHandlerAttribute : Attribute
    {
        public readonly object packet;

        public PacketHandlerAttribute(object packet)
        {
            this.packet = packet;
        }
    }

    public class ServerWorldState : MultiplayerConnectionState
    {
        private static Regex UsernamePattern = new Regex(@"^[a-zA-Z0-9_]+$");

        public ServerWorldState(IConnection connection) : base(connection)
        {
        }

        [PacketHandler(Packets.CLIENT_USERNAME)]
        public void HandleClientUsername(ByteReader data)
        {
            string username = data.ReadString();

            if (username.Length < 3 || username.Length > 15)
            {
                Connection.Close("Invalid username length.");
                return;
            }

            if (!UsernamePattern.IsMatch(username))
            {
                Connection.Close("Invalid username characters.");
                return;
            }

            if (MultiplayerServer.instance.GetPlayer(username) != null)
            {
                Connection.Close("Username already online.");
                return;
            }

            Connection.Username = username;

            MultiplayerServer.instance.SendToAll(Packets.SERVER_NOTIFICATION, new object[] { "Player " + Connection.Username + " has joined the game." });
            MultiplayerServer.instance.UpdatePlayerList();
        }

        [PacketHandler(Packets.CLIENT_REQUEST_WORLD)]
        public void HandleWorldRequest(ByteReader data)
        {
            if (!MultiplayerServer.instance.playerFactions.TryGetValue(Connection.Username, out int factionId))
            {
                factionId = MultiplayerServer.instance.nextUniqueId++;
                MultiplayerServer.instance.playerFactions[Connection.Username] = factionId;

                byte[] extra = ByteWriter.GetBytes(factionId);
                MultiplayerServer.instance.SendCommand(CommandType.SETUP_FACTION, ScheduledCommand.NoFaction, ScheduledCommand.Global, extra);
            }

            if (MultiplayerServer.instance.players.Count(p => p.FactionId == factionId) == 1)
            {
                byte[] extra = ByteWriter.GetBytes(factionId);
                MultiplayerServer.instance.SendCommand(CommandType.FACTION_ONLINE, ScheduledCommand.NoFaction, ScheduledCommand.Global, extra);
            }

            List<byte[]> globalCmds = MultiplayerServer.instance.globalCmds;
            ByteWriter writer = new ByteWriter();

            writer.WriteInt32(factionId);
            writer.WriteInt32(MultiplayerServer.instance.timer);
            writer.Write(globalCmds);
            writer.WritePrefixedBytes(MultiplayerServer.instance.savedGame);

            writer.WriteInt32(1); // maps count

            foreach (int mapId in new[] { 0 })
            {
                MultiplayerServer.instance.SendCommand(CommandType.CREATE_MAP_FACTION_DATA, ScheduledCommand.NoFaction, mapId, ByteWriter.GetBytes(factionId));

                writer.WriteInt32(mapId);
                writer.Write(MultiplayerServer.instance.mapCmds[mapId]);
                writer.WritePrefixedBytes(MultiplayerServer.instance.mapData[mapId]);
            }

            byte[] packetData = writer.GetArray();
            Connection.Send(Packets.SERVER_WORLD_DATA, packetData);

            MpLog.Log("World response sent: " + packetData.Length + " " + globalCmds.Count);
        }

        [PacketHandler(Packets.CLIENT_WORLD_LOADED)]
        public void HandleWorldLoaded(ByteReader data)
        {
            Connection.State = new ServerPlayingState(Connection);
            MultiplayerServer.instance.UpdatePlayerList();
        }

        public override void Disconnected(string reason)
        {
        }
    }

    public class ServerPlayingState : MultiplayerConnectionState
    {
        public ServerPlayingState(IConnection connection) : base(connection)
        {
        }

        [PacketHandler(Packets.CLIENT_COMMAND)]
        public void HandleClientCommand(ByteReader data)
        {
            CommandType cmd = (CommandType)data.ReadInt32();
            int mapId = data.ReadInt32();
            byte[] extra = data.ReadPrefixedBytes();

            // todo check if map id is valid for the player

            int factionId = MultiplayerServer.instance.playerFactions[Connection.Username];
            MultiplayerServer.instance.SendCommand(cmd, factionId, mapId, extra);
        }

        [PacketHandler(Packets.CLIENT_CHAT)]
        public void HandleChat(ByteReader data)
        {
            string msg = data.ReadString();
            msg = msg.Trim();

            if (msg.Length == 0) return;

            MultiplayerServer.instance.SendToAll(Packets.SERVER_CHAT, new object[] { Connection.Username, msg });
        }

        [PacketHandler(Packets.CLIENT_AUTOSAVED_DATA)]
        public void HandleAutosavedData(ByteReader data)
        {
            int type = data.ReadInt32();
            byte[] compressedData = data.ReadPrefixedBytes();

            if (type == 0) // Faction data
            {
                MultiplayerServer.instance.savedGame = compressedData;
            }
            else if (type == 1) // Map data
            {
                int mapId = data.ReadInt32();

                // todo test map ownership
                MultiplayerServer.instance.mapData[mapId] = compressedData;
            }
        }

        [PacketHandler(Packets.CLIENT_ENCOUNTER_REQUEST)]
        public void HandleEncounterRequest(ByteReader data)
        {
            int tile = data.ReadInt32();
            if (!MultiplayerServer.instance.mapTiles.TryGetValue(tile, out int mapId))
                return;

            byte[] extra = ByteWriter.GetBytes(Connection.Username); // todo faction id
            MultiplayerServer.instance.SendCommand(CommandType.CREATE_MAP_FACTION_DATA, ScheduledCommand.NoFaction, mapId, extra);

            byte[] mapData = MultiplayerServer.instance.mapData[mapId];
            List<byte[]> mapCmds = MultiplayerServer.instance.mapCmds.AddOrGet(mapId, new List<byte[]>());

            byte[] packetData = ByteWriter.GetBytes(mapId, mapCmds, mapData);
            Connection.Send(Packets.SERVER_MAP_RESPONSE, packetData);
        }

        [PacketHandler(Packets.CLIENT_ID_BLOCK_REQUEST)]
        public void HandleIdBlockRequest(ByteReader data)
        {
            int mapId = data.ReadInt32();

            if (mapId == ScheduledCommand.Global)
            {
                IdBlock nextBlock = MultiplayerServer.instance.NextIdBlock();
                MultiplayerServer.instance.SendCommand(CommandType.GLOBAL_ID_BLOCK, ScheduledCommand.NoFaction, ScheduledCommand.Global, nextBlock.Serialize());
            }
            else
            {
                // todo
            }
        }

        [PacketHandler(Packets.CLIENT_MAP_LOADED)]
        public void HandleMapLoaded(ByteReader data)
        {
            // todo
        }

        public void OnMessage(Packets packet, ByteReader data)
        {
            /* if (packet == Packets.CLIENT_MAP_STATE_DEBUG)
            {
                OnMainThread.Enqueue(() => { Log.Message("Got map state " + Connection.username + " " + data.GetBytes().Length); });

                ThreadPool.QueueUserWorkItem(s =>
                {
                    using (MemoryStream stream = new MemoryStream(data.GetBytes()))
                    using (XmlTextReader xml = new XmlTextReader(stream))
                    {
                        XmlDocument xmlDocument = new XmlDocument();
                        xmlDocument.Load(xml);
                        xmlDocument.DocumentElement["map"].RemoveChildIfPresent("rememberedCameraPos");
                        xmlDocument.Save(GetPlayerMapsPath(Connection.username + "_replay"));
                        OnMainThread.Enqueue(() => { Log.Message("Writing done for " + Connection.username); });
                    }
                });
            }*/
        }

        public override void Disconnected(string reason)
        {
        }

        public static string GetPlayerMapsPath(string username)
        {
            string worldfolder = Path.Combine(Path.Combine(MultiplayerServer.instance.saveFolder, "MpSaves"), MultiplayerServer.instance.worldId);
            DirectoryInfo directoryInfo = new DirectoryInfo(worldfolder);
            if (!directoryInfo.Exists)
                directoryInfo.Create();
            return Path.Combine(worldfolder, username + ".maps");
        }
    }
}
