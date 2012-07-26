﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Drawing;
using ManicDigger;
using Vector3iG = ManicDigger.Vector3i;
using Vector3iC = ManicDigger.Vector3i;
using PointG = System.Drawing.Point;
using GameModeFortress;
using System.Diagnostics;

namespace ManicDiggerServer
{
    public partial class Server
    {
        //The main function for loading, unloadnig and sending chunks to players.
        private void NotifyMap()
        {
            Stopwatch s = new Stopwatch();
            s.Start();
            int areasizechunks = playerareasize / chunksize;
            int areasizeZchunks = d_Map.MapSizeZ / chunksize;
            int mapsizeXchunks = d_Map.MapSizeX / chunksize;
            int mapsizeYchunks = d_Map.MapSizeY / chunksize;
            int mapsizeZchunks = d_Map.MapSizeZ / chunksize;
            foreach (var k in clients)
            {
                if (k.Value.state == ClientStateOnServer.Connecting)
                {
                    continue;
                }
                Vector3i playerpos = PlayerBlockPosition(k.Value);
                //a) if player is loading, then first generate all (LoadingGenerating), and then send all (LoadingSending)
                //b) if player is playing, then load 1, send 1.
                if (k.Value.state == ClientStateOnServer.LoadingGenerating)
                {
                    var chunksAround = new List<Vector3i>(PlayerAreaChunks(k.Key));
                    //load
                    for (int i = 0; i < chunksAround.Count; i++)
                    {
                        Vector3i v = chunksAround[i];
                        LoadChunk(v.x / chunksize, v.y / chunksize, v.z / chunksize);
                        if (k.Value.state == ClientStateOnServer.LoadingGenerating)
                        {
                            //var a = PlayerArea(k.Key);
                            if (i % 10 == 0)
                            {
                                SendLevelProgress(k.Key, (int)(((float)i / chunksAround.Count) * 100), "Generating world...");
                            }
                        }
                        if (s.ElapsedMilliseconds > 10)
                        {
                            return;
                        }
                    }
                    k.Value.state = ClientStateOnServer.LoadingSending;
                }
                else if (k.Value.state == ClientStateOnServer.LoadingSending)
                {
                    var chunksAround = new List<Vector3i>(PlayerAreaChunks(k.Key));
                    //send
                    for (int i = 0; i < chunksAround.Count; i++)
                    {
                        Vector3i v = chunksAround[i];
                        if (!ClientSeenChunk(k.Key, v.x, v.y, v.z))
                        {
                            SendChunk(k.Key, v);
                            SendLevelProgress(k.Key, (int)(((float)k.Value.maploadingsentchunks++ / chunksAround.Count) * 100), "Downloading map...");
                            if (s.ElapsedMilliseconds > 10)
                            {
                                return;
                            }
                        }
                    }
                    //Finished map loading for a connecting player.
                    bool sent_all_in_range = (k.Value.maploadingsentchunks == chunksAround.Count);
                    if (sent_all_in_range)
                    {
                        SendLevelFinalize(k.Key);
                        clients[k.Key].state = ClientStateOnServer.Playing;
                        clients[k.Key].Ping.TimeoutValue = config.ClientPlayingTimeout;
                    }
                }
                else //b)
                {
                    //inlined PlayerAreaChunks
                    PointG p = PlayerArea(k.Key);
                    Client c = clients[k.Key];
                    int pchunksx = p.X / chunksize;
                    int pchunksy = p.Y / chunksize;
                    for (int x = 0; x < areasizechunks; x++)
                    {
                        for (int y = 0; y < areasizechunks; y++)
                        {
                            for (int z = 0; z < areasizeZchunks; z++)
                            {
                                int vx = pchunksx + x;
                                int vy = pchunksy + y;
                                int vz = z;
                                //if (MapUtil.IsValidPos(d_Map, vx, vy, vz))
                                if (vx >= 0 && vy >= 0 && vz >= 0
                                    && vx < mapsizeXchunks && vy < mapsizeYchunks && vz < mapsizeZchunks)
                                {
                                    LoadAndSendChunk(c, k.Key, vx, vy, vz, s);
                                    if (d_Map.wasChunkGenerated && s.ElapsedMilliseconds > 10)
                                    {
                                        return;
                                    }
                                    d_Map.wasChunkGenerated = false;
                                }
                            }
                        }
                    }
                    //inlined ChunksAroundPlayer
                    //ChunksAroundPlayer is needed for preloading terrain behind current MapArea edges.
                    //chunksAround.AddRange(ChunksAroundPlayer(playerpos));
                    int playerpos2xchunks = (playerpos.x / chunksize);
                    int playerpos2ychunks = (playerpos.y / chunksize);
                    for (int x = -chunkdrawdistance; x <= chunkdrawdistance; x++)
                    {
                        for (int y = -chunkdrawdistance; y <= chunkdrawdistance; y++)
                        {
                            for (int z = 0; z < areasizeZchunks; z++)
                            {
                                int p2x = playerpos2xchunks + x;
                                int p2y = playerpos2ychunks + y;
                                int p2z = z;
                                if (p2x >= 0 && p2y >= 0 && p2z >= 0
                                    && p2x < mapsizeXchunks && p2y < mapsizeYchunks && p2z < mapsizeZchunks)
                                {
                                    LoadAndSendChunk(c, k.Key, p2x, p2y, p2z, s);
                                    if (d_Map.wasChunkGenerated && s.ElapsedMilliseconds > 10)
                                    {
                                        return;
                                    }
                                    d_Map.wasChunkGenerated = false;
                                }
                            }
                        }
                    }
                }
            }
        }

        void LoadAndSendChunk(Client c, int kKey, int vx, int vy, int vz, Stopwatch s)
        {
            //load
            LoadChunk(vx, vy, vz);
            //send
            int pos = MapUtil.Index3d(vx, vy, vz, d_Map.MapSizeX / chunksize, d_Map.MapSizeY / chunksize);
            if (!c.chunksseen[pos])
            {
                SendChunk(kKey, new Vector3i(vx * chunksize, vy * chunksize, vz * chunksize));
            }
        }

        // generates a new spawn near initial spawn if initial spawn is in water
        private Vector3i DontSpawnPlayerInWater(Vector3i initialSpawn)
        {
            if (IsPlayerPositionDry(initialSpawn.x, initialSpawn.y, initialSpawn.z))
            {
                return initialSpawn;
            }

            //find shore
            //bonus +10 because player shouldn't be spawned too close to shore.
            bool bonusset = false;
            int bonus = -1;
            Vector3i pos = initialSpawn;
            for (int i = 0; i < playerareasize / 4 - 5; i++)
            {
                if (IsPlayerPositionDry(pos.x, pos.y, pos.z))
                {
                    if (!bonusset)
                    {
                        bonus = 10;
                        bonusset = true;
                    }
                }
                if (bonusset && bonus-- < 0)
                {
                    break;
                }
                pos.x ++;
                int newblockheight = MapUtil.blockheight(d_Map, 0, pos.x, pos.y);
                pos.z = newblockheight + 1;
            }
            return pos;
        }

        bool IsPlayerPositionDry(int x, int y, int z)
        {
            for (int i = 0; i < 4; i++)
            {
                if (MapUtil.IsValidPos(d_Map, x, y, z - i))
                {
                    int blockUnderPlayer = d_Map.GetBlock(x, y, z - i);
                    if (d_Data.IsFluid[blockUnderPlayer])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        void SendChunk(int clientid, Vector3i v)
        {
            Client c = clients[clientid];
            byte[] chunk = d_Map.GetChunk(v.x, v.y, v.z);
            ClientSeenChunkSet(clientid, v.x, v.y, v.z, (int)simulationcurrentframe);
            //sent++;
            byte[] compressedchunk;
            if (MapUtil.IsSolidChunk(chunk) && chunk[0] == 0)
            {
                //don't send empty chunk.
                compressedchunk = null;
            }
            else
            {
                compressedchunk = CompressChunkNetwork(chunk);
                //todo!
                //commented because it was being sent too early, before full column was generated.
                //if (!c.heightmapchunksseen.ContainsKey(new Vector2i(v.x, v.y)))
                {
                    byte[] heightmapchunk = d_Map.GetHeightmapChunk(v.x, v.y);
                    byte[] compressedHeightmapChunk = d_NetworkCompression.Compress(heightmapchunk);
                    PacketServerHeightmapChunk p1 = new PacketServerHeightmapChunk()
                    {
                        X = v.x,
                        Y = v.y,
                        SizeX = chunksize,
                        SizeY = chunksize,
                        CompressedHeightmap = compressedHeightmapChunk,
                    };
                    SendPacket(clientid, Serialize(new PacketServer() { PacketId = ServerPacketId.HeightmapChunk, HeightmapChunk = p1 }));
                    c.heightmapchunksseen[new Vector2i(v.x, v.y)] = (int)simulationcurrentframe;
                }
            }
            PacketServerChunk p = new PacketServerChunk()
            {
                X = v.x,
                Y = v.y,
                Z = v.z,
                SizeX = chunksize,
                SizeY = chunksize,
                SizeZ = chunksize,
                CompressedChunk = compressedchunk,
            };
            SendPacket(clientid, Serialize(new PacketServer() { PacketId = ServerPacketId.Chunk, Chunk = p }));
        }

        int playerareasize = 256;
        int centerareasize = 128;

        PointG PlayerArea(int playerId)
        {
            return MapUtil.PlayerArea(playerareasize, centerareasize, PlayerBlockPosition(clients[playerId]));
        }

        IEnumerable<Vector3iG> PlayerAreaChunks(int playerId)
        {
            PointG p = PlayerArea(playerId);
            for (int x = 0; x < playerareasize / chunksize; x++)
            {
                for (int y = 0; y < playerareasize / chunksize; y++)
                {
                    for (int z = 0; z < d_Map.MapSizeZ / chunksize; z++)
                    {
                        var v = new Vector3i(p.X + x * chunksize, p.Y + y * chunksize, z * chunksize);
                        if (MapUtil.IsValidPos(d_Map, v.x, v.y, v.z))
                        {
                            yield return v;
                        }
                    }
                }
            }
        }
        // Interfaces to manipulate server's map.
        public void SetBlock(int x, int y, int z, int blocktype)
        {
            if (MapUtil.IsValidPos(d_Map, x, y, z))
            {
                SetBlockAndNotify(x, y, z, blocktype);
            }
        }
        public int GetBlock(int x, int y, int z)
        {
            if (MapUtil.IsValidPos(d_Map, x, y, z))
            {
                return d_Map.GetBlock(x, y, z);
            }
            return 0;
        }
        public int GetHeight(int x, int y)
        {
            return MapUtil.blockheight(d_Map, 0, x, y);
        }
        public void SetChunk(int x, int y, int z, byte[] data)
        {
            if (MapUtil.IsValidPos(d_Map, x, y, z))
            {
                x = x / chunksize;
                y = y / chunksize;
                z = z / chunksize;
                Chunk c = d_Map.chunks[x,y,z];
                if (c == null)
                {
                    c = new Chunk();
                }
                c.data = data;
                c.DirtyForSaving = true;
                d_Map.chunks[x,y,z] = c;
                // update related chunk at clients
                foreach (var k in clients)
                {
                    //todo wrong
                    //k.Value.chunksseen.Clear();
                    Array.Clear(k.Value.chunksseen, 0, k.Value.chunksseen.Length);
                }
            }
        }
        public byte[] GetChunk(int x, int y, int z)
        {
            if (MapUtil.IsValidPos(d_Map, x, y, z))
            {
                x = x / chunksize;
                y = y / chunksize;
                z = z / chunksize;
                return d_Map.chunks[x,y,z].data;
            }
            return null;
        }
        public void DeleteChunk(int x, int y, int z)
        {
            if (MapUtil.IsValidPos(d_Map, x, y, z))
            {
                x = x / chunksize;
                y = y / chunksize;
                z = z / chunksize;
                ChunkDb.DeleteChunk(d_ChunkDb, x, y, z);
                d_Map.chunks[x,y,z] = null;
                // update related chunk at clients
                foreach (var k in clients)
                {
                    //todo wrong
                    //k.Value.chunksseen.Clear();
                    Array.Clear(k.Value.chunksseen, 0, k.Value.chunksseen.Length);
                }
            }
        }
        public void DeleteChunks(List<Vector3i> chunkPositions)
        {
            List<Xyz> chunks = new List<Xyz>();
            foreach (Vector3i pos in chunkPositions)
            {
                if (MapUtil.IsValidPos(d_Map, pos.x, pos.y, pos.z))
                {
                    int x = pos.x / chunksize;
                    int y = pos.y / chunksize;
                    int z = pos.z / chunksize;
                    d_Map.chunks[x,y,z] = null;
                    chunks.Add(new Xyz(){X = x, Y = y, Z = z});
                }
            }
            if (chunks.Count != 0)
            {
                ChunkDb.DeleteChunks(d_ChunkDb, chunks);
                // force to update chunks at clients
                foreach (var k in clients)
                {
                    //todo wrong
                    //k.Value.chunksseen.Clear();
                    Array.Clear(k.Value.chunksseen, 0, k.Value.chunksseen.Length);
                }
            }
        }
        public int[] GetMapSize()
        {
            return new int[] {d_Map.MapSizeX, d_Map.MapSizeY, d_Map.MapSizeZ};
        }
    }
}