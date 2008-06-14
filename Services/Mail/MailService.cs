using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using RiseOp.Implementation;
using RiseOp.Implementation.Dht;
using RiseOp.Implementation.Transport;
using RiseOp.Implementation.Protocol;
using RiseOp.Implementation.Protocol.Net;

using RiseOp.Services.Assist;
using RiseOp.Services.Transfer;

/* files
 *      mail folder
 *          outbound file (mail headers)
 *          inbound file (mail headers)
 *          inbound/outbound mail files
 *      cache folder
 *          cache file
 *              mail headers
 *              acks
 *              pending headers (including local pending header)
 *          mail/pending files (including local pending file)
 * */

/* entry removal
 *      mail map
 *          ID not in source's pending mail list
 *          ID in target's pending ack list
 *      ack map
 *          ID not in target's pending mail list
 *          ID not in source's pending ack list
 *      pending mail
 *          Ack received from target
 *          ID in targets pending ack list
 *      pending ack
 *          ID not in targets pending mail list
 * */

/* re-publish (make sure pending lists of targets updated first)
 *      mail
 *          search for ID returns nothing
 *      ack 
 *          search for ID returns nothing
 * */


namespace RiseOp.Services.Mail
{
    internal delegate void MailUpdateHandler(bool inbox, LocalMail message);

    enum MailBoxType { Inbox, Outbox }


    class MailService : OpService
    {
        public string Name { get { return "Mail"; } }
        public uint ServiceID { get { return 7; } }

        const uint DataTypeLocal = 0x01;
        const uint DataTypePending = 0x02;
        const uint DataTypeMail = 0x03;
        const uint DataTypeAck = 0x04;

        internal OpCore Core;
        G2Protocol Protocol;
        internal DhtNetwork Network;
        internal DhtStore Store;

        internal string MailPath;
        internal string CachePath;
        byte[] LocalFileKey;
        RijndaelManaged MailIDKey;

        internal Dictionary<ulong, List<CachedMail>> MailMap = new Dictionary<ulong, List<CachedMail>>();
        internal Dictionary<ulong, List<CachedAck>>  AckMap  = new Dictionary<ulong, List<CachedAck>>();
        internal Dictionary<ulong, CachedPending> PendingMap = new Dictionary<ulong, CachedPending>();

        internal ThreadedList<LocalMail> Inbox;
        internal ThreadedList<LocalMail> Outbox;

        internal bool SaveInbox;
        internal bool SaveOutbox;

        internal Dictionary<ulong, List<ulong>>  PendingMail = new Dictionary<ulong, List<ulong>>();
        internal Dictionary<ulong, List<byte[]>> PendingAcks = new Dictionary<ulong, List<byte[]>>();

        bool RunSaveHeaders;
        Dictionary<ulong, List<MailIdent>> DownloadMailLater = new Dictionary<ulong,List<MailIdent>>();
        Dictionary<ulong, List<MailIdent>> DownloadAcksLater = new Dictionary<ulong, List<MailIdent>>();

        internal MailUpdateHandler MailUpdate;

        int PruneSize = 64;


        VersionedCache PendingCache;


        internal MailService(OpCore core)
        {
            Core = core;
            Network = core.Network;
            Protocol = Network.Protocol;
            Store = Network.Store;

            Core.SecondTimerEvent += new TimerHandler(Core_SecondTimer);
            Core.MinuteTimerEvent += new TimerHandler(Core_MinuteTimer);

            Network.StatusChange += new StatusChange(Network_StatusChange);

            Store.StoreEvent[ServiceID, DataTypeMail] += new StoreHandler(Store_LocalMail);
            Store.StoreEvent[ServiceID, DataTypeAck] += new StoreHandler(Store_LocalAck);

            Network.Searches.SearchEvent[ServiceID, DataTypeMail] += new SearchRequestHandler(Search_LocalMail);
            Network.Searches.SearchEvent[ServiceID, DataTypeAck] += new SearchRequestHandler(Search_LocalAck);

            Store.ReplicateEvent[ServiceID, DataTypeMail] += new ReplicateHandler(Store_ReplicateMail);
            Store.ReplicateEvent[ServiceID, DataTypeAck] += new ReplicateHandler(Store_ReplicateAck);

            Store.PatchEvent[ServiceID, DataTypeMail] += new PatchHandler(Store_PatchMail);
            Store.PatchEvent[ServiceID, DataTypeAck] += new PatchHandler(Store_PatchAck);

            Core.Transfers.FileSearch[ServiceID, DataTypeMail] += new FileSearchHandler(Transfers_MailSearch);
            Core.Transfers.FileRequest[ServiceID, DataTypeMail] += new FileRequestHandler(Transfers_MailRequest);

            if (Core.Sim != null)
                PruneSize = 16;

            LocalFileKey = Core.User.Settings.FileKey;

            MailIDKey = new RijndaelManaged();
            MailIDKey.Key = LocalFileKey;
            MailIDKey.IV = new byte[MailIDKey.IV.Length];
            MailIDKey.Padding = PaddingMode.None;

            MailPath = Core.User.RootPath + Path.DirectorySeparatorChar + "Data" + Path.DirectorySeparatorChar + ServiceID.ToString();
            CachePath = MailPath + Path.DirectorySeparatorChar + "1";

            Directory.CreateDirectory(MailPath);
            Directory.CreateDirectory(CachePath);

            PendingCache = new VersionedCache(Network, ServiceID, DataTypePending, true);

            PendingCache.FileAquired += new FileAquiredHandler(PendingCache_FileAquired);
            PendingCache.FileRemoved += new FileRemovedHandler(PendingCache_FileRemoved);
            PendingCache.Load();

            LoadHeaders();
        }

        public void Dispose()
        {
            Core.SecondTimerEvent -= new TimerHandler(Core_SecondTimer);
            Core.MinuteTimerEvent -= new TimerHandler(Core_MinuteTimer);

            Network.StatusChange -= new StatusChange(Network_StatusChange);

            Store.StoreEvent[ServiceID, DataTypeMail] -= new StoreHandler(Store_LocalMail);
            Store.StoreEvent[ServiceID, DataTypeAck] -= new StoreHandler(Store_LocalAck);

            Network.Searches.SearchEvent[ServiceID, DataTypeMail] -= new SearchRequestHandler(Search_LocalMail);
            Network.Searches.SearchEvent[ServiceID, DataTypeAck] -= new SearchRequestHandler(Search_LocalAck);

            Store.ReplicateEvent[ServiceID, DataTypeMail] -= new ReplicateHandler(Store_ReplicateMail);
            Store.ReplicateEvent[ServiceID, DataTypeAck] -= new ReplicateHandler(Store_ReplicateAck);

            Store.PatchEvent[ServiceID, DataTypeMail] -= new PatchHandler(Store_PatchMail);
            Store.PatchEvent[ServiceID, DataTypeAck] -= new PatchHandler(Store_PatchAck);

            PendingCache.FileAquired -= new FileAquiredHandler(PendingCache_FileAquired);
            PendingCache.FileRemoved -= new FileRemovedHandler(PendingCache_FileRemoved);
            PendingCache.Dispose();

            Core.Transfers.FileSearch[ServiceID, DataTypeMail] -= new FileSearchHandler(Transfers_MailSearch);
            Core.Transfers.FileRequest[ServiceID, DataTypeMail] -= new FileRequestHandler(Transfers_MailRequest);

        }

        void Core_SecondTimer()
        {
            //crit periodically do search for unacked list so maybe mail/acks can be purged

            // unload outbound / inbound data if no interfaces connected


            if (RunSaveHeaders)
                SaveHeaders();

            if (SaveInbox && Inbox != null)
                SaveLocalHeaders(MailBoxType.Inbox);

            if (SaveOutbox && Outbox != null)
                SaveLocalHeaders(MailBoxType.Outbox);


            // clean download later map
            if (!Network.Established)
            {
                PruneMap(DownloadMailLater);
                PruneMap(DownloadAcksLater);
            }
        }

        void Core_MinuteTimer()
        {
            // prune mail 
            if (MailMap.Count > PruneSize)
            {
                List<ulong> removeIds = new List<ulong>();

                foreach (List<CachedMail> list in MailMap.Values)
                {
                    if (list.Count > PruneSize) // no other way to ident, cant remove by age
                        list.RemoveRange(0, list.Count - PruneSize);

                    foreach (CachedMail mail in list)
                        if (!Network.Routing.InCacheArea(mail.Header.TargetID))
                        {
                            removeIds.Add(mail.Header.TargetID);
                            break;
                        }
                }

                while (removeIds.Count > 0 && MailMap.Count > PruneSize / 2)
                {
                    ulong furthest = Core.UserID;
                    List<CachedMail> mails = MailMap[furthest];

                    foreach (ulong id in removeIds)
                        if ((id ^ Core.UserID) > (furthest ^ Core.UserID))
                            furthest = id;

                    mails = MailMap[furthest];

                    foreach(CachedMail mail in mails)
                        if(mail.Header != null)
                            try { File.Delete(GetCachePath(mail.Header)); }
                            catch { }

                    MailMap.Remove(furthest);
                    removeIds.Remove(furthest);
                    RunSaveHeaders = true;
                }
            }

            // prune acks 
            if (AckMap.Count > PruneSize)
            {
                List<ulong> removeIds = new List<ulong>();

                foreach (List<CachedAck> list in AckMap.Values)
                {
                    if (list.Count > PruneSize) // no other way to ident, cant remove by age
                        list.RemoveRange(0, list.Count - PruneSize);

                    foreach (CachedAck cached in list)
                        if (!Network.Routing.InCacheArea(cached.Ack.TargetID))
                        {
                            removeIds.Add(cached.Ack.TargetID);
                            break;
                        }
                }

                while (removeIds.Count > 0 && AckMap.Count > PruneSize / 2)
                {
                    ulong furthest = Core.UserID;

                    foreach (ulong id in removeIds)
                        if ((id ^ Core.UserID) > (furthest ^ Core.UserID))
                            furthest = id;

                    AckMap.Remove(furthest);
                    removeIds.Remove(furthest);
                    RunSaveHeaders = true;
                }
            }
        }

        void PendingCache_FileRemoved(OpVersionedFile file)
        {
            CachedPending pending = FindPending(file.UserID);

            if (pending != null)
                PendingMap.Remove(file.UserID);
        }

        private void PruneMap(Dictionary<ulong, List<MailIdent>> map)
        {
            if (map.Count < PruneSize)
                return;

            List<ulong> removeIDs = new List<ulong>();

            while (map.Count > 0 && map.Count > PruneSize)
            {
                ulong furthest = Core.UserID;

                // get furthest id
                foreach (ulong id in map.Keys)
                    if ((id ^ Core.UserID) > (furthest ^ Core.UserID))
                        furthest = id;

                // remove one 
                map.Remove(furthest);
            }  
        }

        void Network_StatusChange()
        {
            if (Network.Established)
            {
                // re-publish acks
                foreach (ulong key in AckMap.Keys)
                    foreach (CachedAck ack in AckMap[key])
                        if (ack.Unique && Network.Routing.InCacheArea(key))
                            Store.PublishNetwork(key, ServiceID, DataTypeAck, ack.SignedAck);

                // only download those objects in our local area
                foreach (ulong key in DownloadAcksLater.Keys)
                    if (Network.Routing.InCacheArea(key))
                        foreach (MailIdent ident in DownloadAcksLater[key])
                            StartAckSearch(key, ident.Encode());

                DownloadAcksLater.Clear();

                // re-publish mail
                foreach (ulong key in MailMap.Keys)
                    foreach (CachedMail mail in MailMap[key])
                        if (mail.Unique && Network.Routing.InCacheArea(key))
                            Store.PublishNetwork(key, ServiceID, DataTypeMail, mail.SignedHeader);


                // only download those objects in our local area
                foreach (ulong key in DownloadMailLater.Keys)
                    if (Network.Routing.InCacheArea(key))
                        foreach (MailIdent ident in DownloadMailLater[key])
                            StartMailSearch(key, ident.Encode());

                DownloadMailLater.Clear();
            }

            // disconnected, reset cache to unique
            else
            {
                foreach (ulong key in AckMap.Keys)
                    foreach (CachedAck ack in AckMap[key])
                        ack.Unique = true;


                foreach (ulong key in MailMap.Keys)
                    foreach (CachedMail mail in MailMap[key])
                        mail.Unique = true;
            }
        }

        void SaveHeaders()
        {
            if (Core.InvokeRequired)
                Debug.Assert(false);

            RunSaveHeaders = false;

            try
            {
                string tempPath = Core.GetTempPath();
                CryptoStream stream = IVCryptoStream.Save(tempPath, LocalFileKey);

                // mail headers
                foreach (List<CachedMail> list in MailMap.Values)
                    foreach (CachedMail mail in list)
                        stream.Write(mail.SignedHeader, 0, mail.SignedHeader.Length);

                // acks
                foreach (List<CachedAck> list in AckMap.Values)
                    foreach (CachedAck ack in list)
                        stream.Write(ack.SignedAck, 0, ack.SignedAck.Length);


                stream.FlushFinalBlock();
                stream.Close();


                string finalPath = CachePath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, "headers");
                File.Delete(finalPath);
                File.Move(tempPath, finalPath);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error saving headers " + ex.Message);
            }
        }

        void LoadHeaders()
        {
            try
            {
                string path = CachePath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, "headers");

                if (!File.Exists(path))
                    return;

                CryptoStream crypto = IVCryptoStream.Load(path, LocalFileKey);
                PacketStream stream = new PacketStream(crypto, Network.Protocol, FileAccess.Read);

                G2Header root = null;

                while (stream.ReadPacket(ref root))
                    if (root.Name == DataPacket.SignedData)
                    {
                        SignedData signed = SignedData.Decode(root);
                        G2Header embedded = new G2Header(signed.Data);

                        // figure out data contained
                        if (G2Protocol.ReadPacket(embedded))
                        {
                            if (embedded.Name == MailPacket.MailHeader)
                                Process_MailHeader(null, signed, MailHeader.Decode(embedded));

                            else if (embedded.Name == MailPacket.Ack)
                                Process_MailAck(null, signed, MailAck.Decode(embedded), true);
                        }
                    }

                stream.Close();
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error loading headers " + ex.Message);
            }
        }

        internal void SaveLocalHeaders(MailBoxType box)
        {           
            if (Core.InvokeRequired)
                Debug.Assert(false);

            ThreadedList<LocalMail> mailList = (box == MailBoxType.Inbox) ? Inbox : Outbox;
            string name = (box == MailBoxType.Inbox) ? "inbox" : "outbox";

            if (box == MailBoxType.Inbox)
                SaveInbox = false;
            if (box == MailBoxType.Outbox)
                SaveOutbox = false;

            if (mailList == null)
                return;

 
            try
            {
                string tempPath = Core.GetTempPath();
                CryptoStream stream = IVCryptoStream.Save(tempPath, LocalFileKey);

                mailList.LockReading(delegate()
                {
                    foreach (LocalMail local in mailList)
                    {
                        byte[] encoded = local.Header.Encode(Network.Protocol, true);
                        stream.Write(encoded, 0, encoded.Length);
                    }
                });

                stream.FlushFinalBlock();
                stream.Close();


                string finalPath = MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, name);
                File.Delete(finalPath);
                File.Move(tempPath, finalPath);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error saving " + name + " " + ex.Message);
            }
        }

        internal void LoadLocalHeaders(MailBoxType box)
        {
            ThreadedList<LocalMail> mailList = new ThreadedList<LocalMail>();
            List<MailHeader> headers = new List<MailHeader>();

            string name = (box == MailBoxType.Inbox) ? "inbox" : "outbox";

            string path = MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, name);

            if (File.Exists(path))
                try
                {
                    CryptoStream crypto = IVCryptoStream.Load(path, LocalFileKey);
                    PacketStream stream = new PacketStream(crypto, Network.Protocol, FileAccess.Read);

                    G2Header root = null;

                    while (stream.ReadPacket(ref root))
                        if (root.Name == MailPacket.MailHeader)
                        {
                            MailHeader header = MailHeader.Decode(root);

                            if (header == null)
                                continue;

                            Core.IndexKey(header.SourceID, ref header.Source);

                            if(header.Target != null)
                                Core.IndexKey(header.TargetID, ref header.Target);

                            headers.Add(header);
                        }

                    stream.Close();
                }
                catch (Exception ex)
                {
                    Core.Network.UpdateLog("Mail", "Error loading " + name + " " + ex.Message);
                }

            // load mail files that headers point to
            foreach (MailHeader header in headers)
            {
                LocalMail local = LoadLocalMail(header);

                if (local != null)
                    mailList.SafeAdd(local);
            }

            if (box == MailBoxType.Inbox)
                Inbox = mailList;
            else
                Outbox = mailList;
        }

        private LocalMail LoadLocalMail(MailHeader header)
        {
            LocalMail local = new LocalMail();
            local.Header = header;
            local.From = header.SourceID;

            try
            {
                string path = GetLocalPath(header);

                if (!File.Exists(path))
                    return null;

                TaggedStream file = new TaggedStream(path);
                CryptoStream crypto = IVCryptoStream.Load(file, header.LocalKey);
                PacketStream stream = new PacketStream(crypto, Network.Protocol, FileAccess.Read);

                G2Header root = null;

                while (stream.ReadPacket(ref root))
                {
                    if (root.Name == MailPacket.MailInfo)
                        local.Info = MailInfo.Decode(root);

                    else if (root.Name == MailPacket.MailDest)
                    {
                        MailDestination dest = MailDestination.Decode(root);
                        Core.IndexKey(dest.KeyID, ref dest.Key);

                        if (dest.CC)
                            local.CC.Add(dest.KeyID);
                        else
                            local.To.Add(dest.KeyID);
                    }

                    else if (root.Name == MailPacket.MailFile)
                        local.Attached.Add( MailFile.Decode(root));
                }

                stream.Close();
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error loading local mail " + ex.Message);
            }

            if (local.Info != null)
                return local;

            return null;
        }

        public List<MenuItemInfo> GetMenuInfo(InterfaceMenuType menuType, ulong user, uint project)
        {
            List<MenuItemInfo> menus = new List<MenuItemInfo>();

            if (menuType == InterfaceMenuType.Quick)
            {
                if (user == Core.UserID)
                    return null;

                menus.Add(new MenuItemInfo("Send Mail", MailRes.SendMail, new EventHandler(QuickMenu_View)));
                return menus;
            }

            if (user != Core.UserID)
                return null;

            if (menuType == InterfaceMenuType.Internal)
                menus.Add(new MenuItemInfo("Comm/Mail", MailRes.Icon, new EventHandler(Menu_View)));

            if (menuType == InterfaceMenuType.External)
                menus.Add(new MenuItemInfo("Mail", MailRes.Icon, new EventHandler(Menu_View)));


            return menus;
        }

        internal void QuickMenu_View(object sender, EventArgs args)
        {
            IViewParams node = sender as IViewParams;

            ulong key = 0;

            if (node != null)
                key = node.GetKey();

            // if window already exists to node, show it
            ComposeMail compose = new ComposeMail(this, key);

            Core.InvokeView(true, compose);
        }

        void Menu_View(object sender, EventArgs args)
        {
            IViewParams node = sender as IViewParams;

            if (node == null)
                return;

            if (node.GetKey() != Core.UserID)
                return;

            MailView view = new MailView(this);

            Core.InvokeView(node.IsExternal(), view);
        }

        internal void SendMail(List<ulong> to, List<AttachedFile> files, string subject, string body)
        {
            if (Core.InvokeRequired)
            {
                Core.RunInCoreBlocked(delegate() { SendMail(to, files, subject, body); });
                return;
            }

            // exception handled by compose mail interface
            MailHeader header = new MailHeader();
            header.Source = Core.User.Settings.KeyPublic;
            header.SourceID = Core.UserID;
            header.LocalKey = Utilities.GenerateKey(Core.StrongRndGen, 256);
            header.Read = true;

            // setup temp file
            string tempPath = Core.GetTempPath();
            CryptoStream stream = IVCryptoStream.Save(tempPath, header.LocalKey);
            int written = 0;

            // build mail file
            written += Protocol.WriteToFile(new MailInfo(subject, Core.TimeNow.ToUniversalTime(), files.Count > 0), stream);

            foreach (ulong id in to)
                written += Protocol.WriteToFile(new MailDestination(Core.KeyMap[id], false), stream);

            byte[] bodyBytes = UTF8Encoding.UTF8.GetBytes(body);
            written += Protocol.WriteToFile(new MailFile("body", bodyBytes.Length), stream);

            foreach (AttachedFile attached in files)
                written += Protocol.WriteToFile(new MailFile(attached.Name, attached.Size), stream);

            stream.WriteByte(0); // end packets
            header.FileStart = (ulong)written + 1;

            // write files
            stream.Write(bodyBytes, 0, bodyBytes.Length);

            if (files != null)
            {
                int buffSize = 4096;
                byte[] buffer = new byte[buffSize];

                foreach (AttachedFile attached in files)
                {
                    FileStream embed = new FileStream(attached.FilePath, FileMode.Open, FileAccess.Read);

                    int read = buffSize;
                    while (read == buffSize)
                    {
                        read = embed.Read(buffer, 0, buffSize);
                        stream.Write(buffer, 0, read);
                    }

                    embed.Close();
                }
            }

            stream.FlushFinalBlock();
            stream.Close();


            // finish building header
            Utilities.HashTagFile(tempPath, ref header.FileHash, ref header.FileSize);


            // move file, overwrite if need be, local id used so filename is the same for all targets
            string finalPath = MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, Core.UserID, header.FileHash);
            File.Move(tempPath, finalPath);

            // write header to outbound file
            if (Outbox == null)
                LoadLocalHeaders(MailBoxType.Outbox);

            LocalMail message = LoadLocalMail(header);

            if (message != null)
            {
                Outbox.SafeAdd(message);
                SaveOutbox = true;

                if (MailUpdate != null)
                    Core.RunInGuiThread(MailUpdate, false, message);
            }

            // write headers to outbound cache file
            List<ulong> targets = new List<ulong>();
            targets.AddRange(to);

            // add targets as unacked
            ulong hashID = BitConverter.ToUInt64(header.FileHash, 0);

            List<KeyValuePair<ulong, byte[]>> publishTargets = new List<KeyValuePair<ulong, byte[]>>();
            foreach (ulong id in targets)
            {
                if (!PendingMail.ContainsKey(hashID))
                    PendingMail[hashID] = new List<ulong>();

                PendingMail[hashID].Add(id);
            }
            
            SavePending();

            CachedPending local = FindPending(Core.UserID);
            header.SourceVersion = (local != null) ? local.Header.Version : 0;

            // publish to targets
            foreach (ulong id in targets)
            {
                header.Target = Core.KeyMap[id];
                header.TargetID = id;
                header.FileKey = EncodeFileKey(Utilities.KeytoRsa(Core.KeyMap[id]), header.LocalKey, header.FileStart);
                header.MailID  = GetMailID(hashID, id);

                if (PendingMap.ContainsKey(id))
                    header.TargetVersion = PendingMap[id].Header.Version;

                byte[] signed = SignedData.Encode(Network.Protocol, Core.User.Settings.KeyPair, header.Encode(Network.Protocol, false) );

                if(Network.Established)
                    Core.Network.Store.PublishNetwork(id, ServiceID, DataTypeMail, signed);

                Store_LocalMail(new DataReq(null, id, ServiceID, DataTypeMail, signed)); // cant direct process_header, because header var is being modified
            }

            SaveHeaders();
        }

        // key to decrypt the email file is encoded with the receiver's public key
        byte[] EncodeFileKey(RSACryptoServiceProvider rsa, byte[] crypt, ulong fileStart)
        {
            byte[] buffer = new byte[crypt.Length + 8];

            crypt.CopyTo(buffer, 0);
            BitConverter.GetBytes(fileStart).CopyTo(buffer, crypt.Length);

            return rsa.Encrypt(buffer, false);
        }

        void DecodeFileKey(byte[] enocoded, ref byte[] fileKey, ref ulong fileStart)
        {
            byte[] decoded = Core.User.Settings.KeyPair.Decrypt(enocoded, false);

            if (decoded.Length < (256 / 8) + 8) // 256 bit encryption is 32 bytes plus 8 for fileStart data
                return;

            fileKey = Utilities.ExtractBytes(decoded, 0, 256/8);

            fileStart = BitConverter.ToUInt64(decoded, fileKey.Length);
        }

        internal byte[] GetMailID(ulong hashID, ulong userID)
        {
            // random bytes in MailInfo ensure that the hash ID is different for all messages

            byte[] buffer = new byte[16];

            BitConverter.GetBytes(hashID).CopyTo(buffer, 0);
            BitConverter.GetBytes(userID).CopyTo(buffer, 8);

            ICryptoTransform transform = MailIDKey.CreateEncryptor();
            buffer = transform.TransformFinalBlock(buffer, 0, buffer.Length);

            return buffer;
        }

        private void StartMailSearch(ulong key, byte[] parameters)
        {
            DhtSearch search = Core.Network.Searches.Start(key, "Mail", ServiceID, DataTypeMail, parameters, new EndSearchHandler(EndMailSearch));

            if (search != null)
                search.TargetResults = 2;
        }

        void EndMailSearch(DhtSearch search)
        {
            foreach (SearchValue found in search.FoundValues)
                Store_LocalMail(new DataReq(found.Sources, search.TargetID, ServiceID, 0, found.Value));
        }

        private void StartAckSearch(ulong key, byte[] parameters)
        {
            DhtSearch search = Core.Network.Searches.Start(key, "Mail", ServiceID, DataTypeAck, parameters, new EndSearchHandler(EndAckSearch));

            if (search != null)
                search.TargetResults = 2;
        }

        void EndAckSearch(DhtSearch search)
        {
            foreach (SearchValue found in search.FoundValues)
                Store_LocalAck(new DataReq(found.Sources, search.TargetID, ServiceID, 0, found.Value));
        }

        void Search_LocalMail(ulong key, byte[] parameters, List<byte[]> results)
        {
            MailIdent ident = MailIdent.Decode(parameters, 0);

            CachedMail cached = FindMail(ident);

            if (cached != null)
                results.Add(cached.SignedHeader);
        }

        void Search_LocalAck(ulong key, byte[] parameters, List<byte[]> results)
        {
            MailIdent ident = MailIdent.Decode(parameters, 0);

            CachedAck cached = FindAck(ident);

            if (cached != null)
                results.Add(cached.SignedAck);
        }

        bool Transfers_MailSearch(ulong key, FileDetails details)
        {
            if (MailMap.ContainsKey(key))
            {
                List<CachedMail> list = MailMap[key];

                foreach (CachedMail mail in list)
                    if (details.Size == mail.Header.FileSize && Utilities.MemCompare(details.Hash, mail.Header.FileHash))
                        return true;
            }

            return false;
        }

        string Transfers_MailRequest(ulong key, FileDetails details)
        {
            if (MailMap.ContainsKey(key))
            {
                List<CachedMail> list = MailMap[key];

                foreach (CachedMail mail in list)
                    if (details.Size == mail.Header.FileSize && Utilities.MemCompare(details.Hash, mail.Header.FileHash))
                    {
                        string path = GetCachePath(mail.Header);
                        if (File.Exists(path))
                            return path;

                        path = GetLocalPath(mail.Header);
                        if (File.Exists(path))
                            return path;
                    }
            }

            return null;
        }

        void Store_LocalMail(DataReq store)
        {
            // getting published to - search results - patch

            SignedData signed = SignedData.Decode(store.Data);

            if (signed == null)
                return;

            G2Header embedded = new G2Header(signed.Data);

            // figure out data contained
            if (G2Protocol.ReadPacket(embedded))
                if (embedded.Name == MailPacket.MailHeader)
                    Process_MailHeader(store, signed, MailHeader.Decode(embedded));
        }

        void Store_LocalAck(DataReq store)
        {
            // getting published to - search results - patch

            SignedData signed = SignedData.Decode(store.Data);

            if (signed == null)
                return;

            G2Header embedded = new G2Header(signed.Data);

            // figure out data contained
            if (G2Protocol.ReadPacket(embedded))
                if (embedded.Name == MailPacket.Ack)
                    Process_MailAck(store, signed, MailAck.Decode(embedded), false);
        }

        List<byte[]> Store_ReplicateMail(DhtContact contact)
        {
            if (!Network.Established)
                return null;


            List<byte[]> patches = new List<byte[]>();

            foreach (List<CachedMail> list in MailMap.Values)
                foreach (CachedMail cached in list)
                    if( Network.Routing.InCacheArea(cached.Header.TargetID))
                        patches.Add(new MailIdent(cached.Header.TargetID, Utilities.ExtractBytes(cached.Header.MailID, 0, 4)).Encode());

            return patches;
        }

        List<byte[]> Store_ReplicateAck(DhtContact contact)
        {
            if (!Network.Established)
                return null;


            List<byte[]> patches = new List<byte[]>();

            // acks
            foreach (List<CachedAck> list in AckMap.Values)
                foreach (CachedAck cached in list)
                    if(Network.Routing.InCacheArea(cached.Ack.TargetID))
                        patches.Add( new MailIdent(cached.Ack.TargetID, Utilities.ExtractBytes(cached.Ack.MailID, 0, 4)).Encode());

            return patches;
        }

        void Store_PatchMail(DhtAddress source, byte[] data)
        {
            if (data.Length % MailIdent.SIZE != 0)
                return;

            int offset = 0;
            for (int i = 0; i < data.Length; i += MailIdent.SIZE)
            {
                MailIdent ident = MailIdent.Decode(data, i);
                offset += MailIdent.SIZE;

                if (!Network.Routing.InCacheArea(ident.UserID))
                    continue;

                CachedMail mail = FindMail(ident);

                if (mail != null)
                {
                    mail.Unique = false;
                    continue;
                }

                if (Network.Established)
                    Network.Searches.SendDirectRequest(source, ident.UserID, ServiceID, DataTypeMail, ident.Encode());
                else
                {
                    if (!DownloadMailLater.ContainsKey(ident.UserID))
                        DownloadMailLater[ident.UserID] = new List<MailIdent>();

                    DownloadMailLater[ident.UserID].Add(ident);
                }
            }
        }

        void Store_PatchAck(DhtAddress source, byte[] data)
        {
            if (data.Length % MailIdent.SIZE != 0)
                return;

            int offset = 0;
            for (int i = 0; i < data.Length; i += MailIdent.SIZE)
            {
                MailIdent ident = MailIdent.Decode(data, i);
                offset += MailIdent.SIZE;

                if (!Network.Routing.InCacheArea(ident.UserID))
                    continue;

                CachedAck ack = FindAck(ident);

                if (ack != null)
                {
                    ack.Unique = false;
                    continue;
                }

                if (Network.Established)
                    Network.Searches.SendDirectRequest(source, ident.UserID, ServiceID, DataTypeAck, ident.Encode());
                else
                {
                    if (!DownloadAcksLater.ContainsKey(ident.UserID))
                        DownloadAcksLater[ident.UserID] = new List<MailIdent>();

                    DownloadAcksLater[ident.UserID].Add(ident);
                }
            }
        }

        CachedMail FindMail(MailIdent ident)
        {
            if (MailMap.ContainsKey(ident.UserID))
                foreach (CachedMail cached in MailMap[ident.UserID])
                    if (Utilities.MemCompare(cached.Header.MailID, 0, ident.Data, 0, 4))
                        return cached;

            return null;
        }

        CachedAck FindAck(MailIdent ident)
        {
            if (AckMap.ContainsKey(ident.UserID))
                foreach (CachedAck cached in AckMap[ident.UserID])
                    if (Utilities.MemCompare(cached.Ack.MailID, 0, ident.Data, 0, 4))
                        return cached;

            return null;
        }

        private CachedPending FindPending(ulong id)
        {
            if (PendingMap.ContainsKey(id))
                return PendingMap[id];

            return null;
        }

        private void Process_MailHeader(DataReq data, SignedData signed, MailHeader header)
        {
            Core.IndexKey(header.SourceID, ref header.Source);
            Core.IndexKey(header.TargetID, ref header.Target);

            // check if already cached
            if (MailMap.ContainsKey(header.TargetID))
                foreach (CachedMail cached in MailMap[header.TargetID])
                    if (Utilities.MemCompare(header.MailID, cached.Header.MailID))
                        return;

            bool cache = false;

            // source's pending file
            if (PendingMap.ContainsKey(header.SourceID))
            {
                CachedPending pending = PendingMap[header.SourceID];

                if (header.SourceVersion > pending.Header.Version)
                {
                    PendingCache.Research(header.SourceID);
                    cache = true;
                }
                else
                {
                    
                    if (IsMailPending(pending, header.MailID))
                        cache = true;
                    else
                        return; //ID not in source's pending mail list
                }
            }
            else
            {
                PendingCache.Research(header.SourceID);
                cache = true;
            }

            // check target's pending file
            if (PendingMap.ContainsKey(header.TargetID))
            {
                CachedPending pending = PendingMap[header.TargetID];

                if (header.TargetVersion > pending.Header.Version)
                {
                    PendingCache.Research(header.TargetID);
                    cache = true;
                }
                else
                {
                    if (IsAckPending(pending, header.MailID))
                        return;  //ID in target's pending ack list
                    else
                        cache = true;
                }
            }
            else
            {
                PendingCache.Research(header.TargetID);
                cache = true;
            }

            if(cache)
                CacheMail(signed, header);
        }

        private void Download_Mail(SignedData signed, MailHeader header)
        {
            if (!Utilities.CheckSignedData(header.Source, signed.Data, signed.Signature))
                return;

            FileDetails details = new FileDetails(ServiceID, DataTypeMail, header.FileHash, header.FileSize, null);

            Core.Transfers.StartDownload(header.TargetID, details, new object[] { signed, header }, new EndDownloadHandler(EndDownload_Mail));
        }

        private void EndDownload_Mail(string path, object[] args)
        {
            SignedData signedHeader = (SignedData)args[0];
            MailHeader header = (MailHeader)args[1];

            try
            {
                File.Move(path, GetCachePath(header));
            }
            catch { return; }

            CacheMail(signedHeader, header);
        }

        private void CacheMail(SignedData signed, MailHeader header)
        {
            if (Core.InvokeRequired)
                Debug.Assert(false);

            try
            {
                bool exists = false;

                // try cache path
                string path = GetCachePath(header);
                if (File.Exists(path))
                    exists = true;

                // try local path
                if (!exists)
                {
                    path = GetLocalPath(header);
                    if (File.Exists(path))
                        exists = true;
                }

                if(!exists)
                {
                    Download_Mail(signed, header);
                    return;
                }

                if(header.TargetID == Core.UserID)
                {
                    ReceiveMail(path, signed, header);
                    return;
                }

                if(!MailMap.ContainsKey(header.TargetID))
                    MailMap[header.TargetID] = new List<CachedMail>();

                CachedMail mail = new CachedMail() ;

                mail.Header = header;
                mail.SignedHeader = signed.Encode(Network.Protocol);
                mail.Unique = !Network.Established;

                MailMap[header.TargetID].Add(mail);

                RunSaveHeaders = true;
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error loading mail " + ex.Message);
            }
        }

        private void ReceiveMail(string path, SignedData signed, MailHeader header)
        {
            header.Received = Core.TimeNow.ToUniversalTime();

            // move file
            string localPath = GetLocalPath(header);
            string cachePath = GetCachePath(header);
           
            if (File.Exists(localPath))
                return;

            File.Move(cachePath, localPath);

            // add to inbound list
            if (Inbox == null)
                LoadLocalHeaders(MailBoxType.Inbox);

            DecodeFileKey(header.FileKey, ref header.LocalKey, ref header.FileStart);

            LocalMail message = LoadLocalMail(header);

            if (message != null)
            {
                Inbox.SafeAdd(message);

                SaveInbox = true;

                if (MailUpdate != null)
                    Core.RunInGuiThread(MailUpdate, true, message);
            }

            // publish ack
            MailAck ack  = new MailAck();
            ack.MailID   = header.MailID;
            ack.Source   = Core.User.Settings.KeyPublic;
            ack.SourceID = Core.UserID;
            ack.Target   = header.Source;
            ack.TargetID = header.TargetID; 
            ack.TargetVersion = header.SourceVersion;
            
            // update pending
            if (!PendingAcks.ContainsKey(ack.TargetID))
                PendingAcks[ack.TargetID] = new List<byte[]>();
            PendingAcks[ack.TargetID].Add(ack.MailID);

            CachedPending local = FindPending(Core.UserID);
            ack.SourceVersion = (local != null) ? local.Header.Version : 0;
            
            SavePending(); // also publishes

            // send 
            byte[] signedAck = SignedData.Encode(Network.Protocol, Core.User.Settings.KeyPair, ack.Encode(Network.Protocol));
            
            if(Network.Established)
                Core.Network.Store.PublishNetwork(header.SourceID, ServiceID, DataTypeAck, signedAck);

            Store_LocalAck(new DataReq(null, header.SourceID, ServiceID, DataTypeAck, signedAck)); // cant direct process_header, because header var is being modified
            RunSaveHeaders = true;
            
            Core.MakeNews("Mail Received from " + Core.Links.GetName(message.From), message.From, 0, false, MailRes.Icon, Menu_View);
         
        }

        private bool IsMailPending(CachedPending pending, byte[] mailID)
        {
            try
            {
                string path = PendingCache.GetFilePath(pending.Header);

                TaggedStream file = new TaggedStream(path);
                CryptoStream crypto = IVCryptoStream.Load(file, pending.Header.FileKey);

                byte[] divider = new byte[16];
                byte[] buffer = new byte[4096];

                int read = buffer.Length;
                while (read == buffer.Length)
                {
                    read = crypto.Read(buffer, 0, buffer.Length);

                    if (read % 16 != 0)
                        return false;

                    for (int i = 0; i < read; i += 16)
                    {
                        if (Utilities.MemCompare(divider, 0, buffer, i, 16))
                            return false;

                        if (Utilities.MemCompare(mailID, 0, buffer, i, 16))
                        {
                            crypto.Close();
                            return true;
                        }
                    }
                }

                crypto.Close();
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error checking pending " + ex.Message);
            }

            return false;
        }

        private bool IsAckPending(CachedPending pending, byte[] mailID)
        {
            try
            {
                string path = PendingCache.GetFilePath(pending.Header);

                TaggedStream file = new TaggedStream(path);
                CryptoStream crypto = IVCryptoStream.Load(file, pending.Header.FileKey);

                bool dividerReached = false;
                byte[] divider = new byte[16];
                byte[] buffer = new byte[4096];

                int read = buffer.Length;
                while (read == buffer.Length)
                {
                    read = crypto.Read(buffer, 0, buffer.Length);

                    if (read % 16 != 0)
                        return false;

                    for (int i = 0; i < read; i += 16)
                    {
                        if (Utilities.MemCompare(divider, 0, buffer, i, 16))
                            dividerReached = true;

                        else if (dividerReached && Utilities.MemCompare(mailID, 0, buffer, i, 16))
                        {
                            crypto.Close();
                            return true;
                        }
                    }
                }

                crypto.Close();
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error checking pending " + ex.Message);
            }

            return false;
        }

        private void Process_MailAck(DataReq data, SignedData signed, MailAck ack, bool verified)
        {
            Core.IndexKey(ack.SourceID, ref ack.Source);
            Core.IndexKey(ack.TargetID, ref ack.Target);

            if (!verified)
                if (!Utilities.CheckSignedData(ack.Source, signed.Data, signed.Signature))
                    return;

            // check if local
            if(ack.TargetID == Core.UserID)
            {
                ReceiveAck(ack);
                return;
            }

            // check if already cached
            if (AckMap.ContainsKey(ack.TargetID))
                foreach (CachedAck cached in AckMap[ack.TargetID])
                    if (Utilities.MemCompare(ack.MailID, cached.Ack.MailID))
                        return;

            bool add = true;

            // check target pending file
            if (PendingMap.ContainsKey(ack.TargetID))
            {
                CachedPending pending = PendingMap[ack.TargetID];

                // if our version of the pending file is out of date
                if (ack.TargetVersion > pending.Header.Version)
                    PendingCache.Research(ack.TargetID);

                // not pending, means its not in file, which means it IS acked
                else if (!IsMailPending(pending, ack.MailID))
                    add = false;
            }
            else
                PendingCache.Research(ack.TargetID);

            // check source pending file, but dont search for it or anything, focus on the target, source is backup
            if (PendingMap.ContainsKey(ack.SourceID))
            {
                CachedPending pending = PendingMap[ack.SourceID];

                if (pending.Header.Version > ack.SourceVersion)
                    if (!IsAckPending(pending, ack.MailID))
                        add = false;
            }

            if(add)
            {
                if (!AckMap.ContainsKey(ack.TargetID))
                    AckMap[ack.TargetID] = new List<CachedAck>();

                AckMap[ack.TargetID].Add(new CachedAck(signed.Encode(Network.Protocol), ack, !Network.Established));
            }
        }

        private void ReceiveAck(MailAck ack)
        {
            ulong hashID, userID;
            DecodeMailID(ack.MailID, out hashID, out userID);

            if (userID != ack.SourceID)
                return;

            if (!PendingMail.ContainsKey(hashID))
                return;

            if (!PendingMail[hashID].Contains(userID))
                return;

            PendingMail[hashID].Remove(userID);

            if (PendingMail[hashID].Count == 0)
                PendingMail.Remove(hashID);

            SavePending(); // also publishes

            // update interface
            if (Outbox != null && MailUpdate != null)
                Outbox.LockReading(delegate()
                {
                    foreach (LocalMail message in Outbox)
                        if (Utilities.MemCompare(message.Header.MailID, ack.MailID))
                        {
                            Core.RunInGuiThread(MailUpdate, false, message);
                            break;
                        }
                });
        }


        void PendingCache_FileAquired(OpVersionedFile cachedfile)
        {
            try
            {
                if (!PendingMap.ContainsKey(cachedfile.UserID))
                    PendingMap[cachedfile.UserID] = new CachedPending(cachedfile);

                CachedPending pending = PendingMap[cachedfile.UserID];

                List<byte[]> pendingMailIDs = new List<byte[]>();
                List<byte[]> pendingAckIDs = new List<byte[]>();


                // load pending file
                TaggedStream file = new TaggedStream(PendingCache.GetFilePath(cachedfile.Header));
                CryptoStream crypto = IVCryptoStream.Load(file, cachedfile.Header.FileKey);

                bool dividerPassed = false;
                byte[] divider = new byte[16];
                byte[] buffer = new byte[4096];
                
                int read = buffer.Length;
                while (read == buffer.Length)
                {
                    read = crypto.Read(buffer, 0, buffer.Length);

                    if (read % 16 != 0)
                        throw new Exception("Bad read pending file");

                    for (int i = 0; i < read; i += 16)
                    {
                        byte[] id = Utilities.ExtractBytes(buffer, i, 16);

                        if (Utilities.MemCompare(id, divider))
                            dividerPassed = true;

                        else if (!dividerPassed)
                            pendingMailIDs.Add(id);

                        else
                            pendingAckIDs.Add(id);
                    }  
                }
                
                crypto.Close();


                // if the local pending file that we are loading
                if (cachedfile.UserID == Core.UserID)
                {
                    // setup local mail pending
                    PendingMail.Clear();

                    foreach (byte[] id in pendingMailIDs)
                    {
                        ulong hashID, userID;
                        DecodeMailID(id, out hashID, out userID);

                        if (!PendingMail.ContainsKey(hashID))
                            PendingMail[hashID] = new List<ulong>();

                        PendingMail[hashID].Add(userID);
                    }

                    // setup local acks pending
                    PendingAcks.Clear();

                    List<ulong> targets = new List<ulong>();

                    string localpath = MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, "acktargets");

                    if (File.Exists(localpath))
                    {
                        file = new TaggedStream(localpath);
                        crypto = IVCryptoStream.Load(file, LocalFileKey);

                        read = buffer.Length;
                        while (read == buffer.Length)
                        {
                            read = crypto.Read(buffer, 0, buffer.Length);

                            if (read % 8 != 0)
                                throw new Exception("Bad read targets file");

                            for (int i = 0; i < read; i += 8)
                                targets.Add(BitConverter.ToUInt64(buffer, i));
                        }

                        crypto.Close();

                        for (int i = 0; i < targets.Count; i++)
                        {
                            if (!PendingAcks.ContainsKey(targets[i]))
                                PendingAcks[targets[i]] = new List<byte[]>();

                            PendingAcks[targets[i]].Add(pendingAckIDs[i]);
                        }
                    }
                }   

                CheckCache(pending, pendingMailIDs, pendingAckIDs);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Mail", "Error loading pending " + ex.Message);
            }
        }

        private void DecodeMailID(byte[] encrypted, out ulong hashID, out ulong userID)
        {
            ICryptoTransform transform = MailIDKey.CreateDecryptor();
            encrypted = transform.TransformFinalBlock(encrypted, 0, encrypted.Length);

            hashID = BitConverter.ToUInt64(encrypted, 0);
            userID  = BitConverter.ToUInt64(encrypted, 8);
        }

        private void CheckCache(CachedPending pending, List<byte[]> mailIDs, List<byte[]> ackIDs)
        {
/* entry removal
 *      mail map
 *          ID not in source's pending mail list X
 *          ID in target's pending ack list X
 *      ack map
 *          ID not in target's pending mail list X
 *          ID not in source's pending ack list X
 * 
 *      pending mail
 *          Ack received from target
 *          ID in targets pending ack list
 *      pending ack
 *          ID not in targets pending mail list
 * */

            VersionedFileHeader header = pending.Header;

            List<string> removePaths = new List<string>();

            // check mail
            List<CachedMail> removeMails = new List<CachedMail>();    
            List<ulong> removeKeys = new List<ulong>();

            foreach (ulong key in MailMap.Keys)
            {
                List<CachedMail> list = MailMap[key];
                removeMails.Clear();

                foreach (CachedMail mail in list)
                {
                    // ID not in source's pending mail list
                    if (mail.Header.SourceID == header.KeyID &&
                        header.Version >= mail.Header.SourceVersion &&
                        !IDinList(mailIDs, mail.Header.MailID))
                        removeMails.Add(mail);
                    

                    // ID in target's pending ack list
                    if (mail.Header.TargetID == pending.Header.KeyID &&
                        header.Version >= mail.Header.TargetVersion &&
                        IDinList(ackIDs, mail.Header.MailID))
                        removeMails.Add(mail);
                }

                foreach (CachedMail mail in removeMails)
                {
                    removePaths.Add(GetCachePath(mail.Header));
                    list.Remove(mail);
                }

                if (list.Count == 0)
                    removeKeys.Add(key);
            }

            foreach (ulong key in removeKeys)
                MailMap.Remove(key);
        

            List<CachedAck> removeAcks = new List<CachedAck>();
            removeKeys.Clear();
            
            // check acks            
            foreach (ulong key in AckMap.Keys)
            {
                List<CachedAck> list = AckMap[key];
                removeAcks.Clear();

                foreach (CachedAck cached in list)
                {
                    // ID not in target's pending mail list
                    if (cached.Ack.TargetID == header.KeyID &&
                        header.Version >= cached.Ack.TargetVersion &&
                        !IDinList(mailIDs, cached.Ack.MailID))
                        removeAcks.Add(cached);

                    // ID not in source's pending ack list
                    if (cached.Ack.SourceID == header.KeyID &&
                        header.Version >= cached.Ack.SourceVersion &&
                        !IDinList(ackIDs, cached.Ack.MailID))
                        removeAcks.Add(cached);
                }
                foreach (CachedAck ack in removeAcks)
                    list.Remove(ack);

                if (list.Count == 0)
                    removeKeys.Add(key);
            }

            foreach (ulong key in removeKeys)
                AckMap.Remove(key);
        

            // mark pending changed and re-save pending
            bool resavePending = false;

            // pending mail, ID in target's pending ack list
            List<ulong> removeTargets = new List<ulong>();
            removeKeys.Clear();

            foreach (ulong hashID in PendingMail.Keys)
            {
                removeTargets.Clear();

                foreach (ulong target in PendingMail[hashID])
                    if (header.KeyID == target)
                    {
                        byte[] mailID = GetMailID(hashID, target);

                        if (IDinList(ackIDs, mailID))
                        {
                            removeTargets.Add(target); // dont have to remove file cause its local
                            resavePending = true;
                        }
                    }

                foreach (ulong target in removeTargets)
                    PendingMail[hashID].Remove(target);

                if (PendingMail[hashID].Count == 0)
                    removeKeys.Add(hashID);
            }

            foreach (ulong hashID in removeKeys)
                PendingMail.Remove(hashID);

            // pending ack, ID not in targets pending mail list
            foreach (ulong target in PendingAcks.Keys)
                if (target == header.KeyID)
                {
                    List<byte[]> removeIDs = new List<byte[]>();

                    foreach (byte[] mailID in PendingAcks[target])
                        if (!IDinList(mailIDs, mailID))
                        {
                            removeIDs.Add(mailID);
                            resavePending = true;
                        }

                    foreach (byte[] mailID in removeIDs)
                        PendingAcks[target].Remove(mailID);

                    if (PendingAcks[target].Count == 0)
                        PendingAcks.Remove(target);

                    break;
                }
                     

            // remove files
            foreach(string path in removePaths)
            {
                try
                {
                    File.Delete(path);
                }
                catch {}
            }

            if (resavePending)
                SavePending();
        }

        private bool IDinList(List<byte[]> ids, byte[] find)
        {
            foreach (byte[] id in ids)
                if (Utilities.MemCompare(id, find))
                    return true;

            return false;
        }

        private void SavePending()
        {
            // pending file is shared over the network, the first part is pending mail IDs
            // the next part is pending ack IDs, because IDs are encrypted ack side needs to seperately
            // store the target ID of the ack locally, local host is only able to decrypt pending mail IDs

            try
            {
                string tempPath = Core.GetTempPath();
                byte[] key = Utilities.GenerateKey(Core.StrongRndGen, 256);
                CryptoStream stream = IVCryptoStream.Save(tempPath, key);

                byte[] buffer = null;

                // write pending mail
                foreach(ulong hashID in PendingMail.Keys)
                    foreach (ulong userID in PendingMail[hashID])
                    {
                        buffer = GetMailID(hashID, userID);
                        stream.Write(buffer, 0, buffer.Length);
                    }

                // divider
                buffer = new byte[16];
                stream.Write(buffer, 0, buffer.Length);

                // write pending acks
                foreach (ulong target in PendingAcks.Keys)
                    foreach (byte[] mailID in PendingAcks[target])
                        stream.Write(mailID, 0, mailID.Length);

                stream.FlushFinalBlock();
                stream.Close();


                // save pending ack targets in local file
                string localpath = MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, "acktargets");

                if (PendingAcks.Count > 0)
                {
                    stream = IVCryptoStream.Save(localpath, LocalFileKey);

                    foreach (ulong target in PendingAcks.Keys)
                        stream.Write(BitConverter.GetBytes(target), 0, 8);

                    stream.FlushFinalBlock();
                    stream.Close();
                }
                else if (File.Exists(localpath))
                    File.Delete(localpath);

                PendingCache.UpdateLocal(tempPath, key, null);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Profile", "Error saving pending " + ex.Message);
            }
        }

        internal string GetCachePath(MailHeader header)
        {
            return CachePath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, header.SourceID, header.FileHash);
        }

        internal string GetLocalPath(MailHeader header)
        {
            return MailPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, header.SourceID, header.FileHash);
        }

        internal void Reply(LocalMail message, string body)
        {
            ComposeMail compose = new ComposeMail(this, message.Header.SourceID);

            string subject = message.Info.Subject;
            if (!subject.StartsWith("RE: "))
                subject = "RE: " + subject;

            compose.SubjectTextBox.Text = subject;

            string header = "\n\n-----Original Message-----\n";
            header += "From: " + Core.Links.GetName(message.Header.SourceID) + "\n";
            header += "Sent: " + Utilities.FormatTime(message.Info.Date) + "\n";
            header += "To: " + GetNames(message.To) + "\n";

            if(message.CC.Count > 0)
                header += "CC: " + GetNames(message.CC) + "\n";

            header += "Subject: " + message.Info.Subject + "\n\n";

            compose.MessageBody.InputBox.AppendText(header);

            compose.MessageBody.InputBox.Select(compose.MessageBody.InputBox.TextLength, 0);
            compose.MessageBody.InputBox.SelectedRtf = body;

            compose.MessageBody.InputBox.Select(0, 0);

            
            Core.RunInGuiThread(Core.GuiMain.ShowExternal, compose);
        }

        internal void Forward(LocalMail message, string body)
        {
            ComposeMail compose = new ComposeMail(this, 0);


            string subject = message.Info.Subject;
            if (!subject.StartsWith("FW: "))
                subject = "FW: " + subject;

            compose.SubjectTextBox.Text = subject;

            //crit attach files


            string header = "\n\n-----Original Message-----\n";
            header += "From: " + Core.Links.GetName(message.Header.SourceID) + "\n";
            header += "Sent: " + Utilities.FormatTime(message.Info.Date) + "\n";
            header += "To: " + GetNames(message.To) + "\n";

            if (message.CC.Count > 0)
                header += "CC: " + GetNames(message.CC) + "\n";

            header += "Subject: " + message.Info.Subject + "\n\n";

            compose.MessageBody.InputBox.AppendText(header);

            compose.MessageBody.InputBox.Select(compose.MessageBody.InputBox.TextLength, 0);
            compose.MessageBody.InputBox.SelectedRtf = body;

            compose.MessageBody.InputBox.Select(0, 0);

            Core.RunInGuiThread(Core.GuiMain.ShowExternal, compose);
        }

        internal void DeleteLocal(LocalMail message, bool inbox)
        {
            File.Delete(GetLocalPath(message.Header));
            
            if (inbox)
            {
                Inbox.SafeRemove(message);
                SaveInbox = true;
            }

            else
            {
                Outbox.SafeRemove(message);
                SaveOutbox = true;
            }
        }

        internal string GetNames(List<ulong> list)
        {
            string names = "";

            foreach (ulong id in list)
                names += Core.Links.GetName(id) + ", ";

            names = names.TrimEnd(new char[] { ' ', ',' });

            return names;
        }
    }

    internal class CachedPending
    {
        OpVersionedFile File;

        internal CachedPending(OpVersionedFile file)
        {
            File = file;
        }

        internal VersionedFileHeader Header
        {
            get
            {
                return File.Header;
            }
        }
    }

    internal class CachedMail
    {
        internal MailHeader Header;
        internal byte[]     SignedHeader;
        internal bool       Unique;
    }

    internal class CachedAck
    {
        internal MailAck Ack;
        internal byte[]  SignedAck;
        internal bool    Unique;

        internal CachedAck(byte[] signed, MailAck ack, bool unique)
        {
            SignedAck = signed;
            Ack = ack;
            Unique = unique;
        }
    }

    internal class MailIdent
    {
        internal const int SIZE = 12;

        internal ulong  UserID;
        internal byte[] Data;

        internal MailIdent()
        { 
        }

        internal MailIdent(ulong id, byte[] data)
        {
            UserID  = id;
            Data   = data;
        }

        internal byte[] Encode()
        {
            byte[] encoded = new byte[SIZE];

            BitConverter.GetBytes(UserID).CopyTo(encoded, 0);
            Data.CopyTo(encoded, 8);

            return encoded;
        }

        internal static MailIdent Decode(byte[] data, int offset)
        {
            MailIdent ident = new MailIdent();

            ident.UserID  = BitConverter.ToUInt64(data, offset);
            ident.Data   = Utilities.ExtractBytes(data, offset + 8, 4);

            return ident;
        }
    }

    internal class LocalMail
    {
        internal MailInfo Info;

        internal ulong From;
        internal List<ulong> To = new List<ulong>();
        internal List<ulong> CC = new List<ulong>();

        internal List<MailFile> Attached = new List<MailFile>();

        internal MailHeader Header;
    }
}
