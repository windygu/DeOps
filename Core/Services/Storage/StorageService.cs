using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using DeOps.Implementation;
using DeOps.Implementation.Dht;
using DeOps.Implementation.Protocol;
using DeOps.Implementation.Protocol.Net;

using DeOps.Services.Assist;
using DeOps.Services.Location;
using DeOps.Services.Transfer;
using DeOps.Services.Trust;
using DeOps.Utility;


namespace DeOps.Services.Storage
{
    public delegate void StorageUpdateHandler(OpStorage storage);
    public delegate void WorkingUpdateHandler(uint project, string dir, ulong uid, WorkingChange action);


    public class StorageService : OpService
    {
        public string Name { get { return "File System"; } }
        public uint ServiceID { get { return (uint)ServiceIDs.Storage; } }

        const uint FileTypeCache = 0x01;
        const uint FileTypeData = 0x02;
        const uint FileTypeWorking = 0x03;
        const uint FileTypeResource = 0x04;

        public OpCore Core;
        public G2Protocol Protocol;
        public DhtNetwork Network;
        public DhtStore Store;
        public TrustService Trust;

        bool Loading = true;
        List<string> ReferencedPaths = new List<string>();

        public string DataPath;
        public string WorkingPath;
        public string ResourcePath;

        public byte[] LocalFileKey;
        RijndaelManaged FileCrypt = new RijndaelManaged();

        bool SavingLocal;
        public StorageUpdateHandler StorageUpdate;

        public ThreadedDictionary<ulong, OpStorage> StorageMap = new ThreadedDictionary<ulong, OpStorage>();
        public ThreadedDictionary<ulong, OpFile> FileMap = new ThreadedDictionary<ulong, OpFile>();
        public ThreadedDictionary<ulong, OpFile> InternalFileMap = new ThreadedDictionary<ulong, OpFile>();// used to bring together files encrypted with different keys

        VersionedCache Cache;

        // working
        public Dictionary<uint, WorkingStorage> Working = new Dictionary<uint, WorkingStorage>();

        public WorkingUpdateHandler WorkingFileUpdate;
        public WorkingUpdateHandler WorkingFolderUpdate;

        public WorkerQueue UnlockFiles = new WorkerQueue("Storage Copy");
        public WorkerQueue CopyFiles = new WorkerQueue("Storage Copy");
        public WorkerQueue HashFiles = new WorkerQueue("Storage Hash");

        public delegate void DisposingHandler();
        public DisposingHandler Disposing;


        public StorageService(OpCore core)
        {
            Core = core;
            Network = core.Network;
            Protocol = Network.Protocol;
            Store = Network.Store;
            Trust = Core.Trust;

            Core.SecondTimerEvent += Core_SecondTimer;
            Core.MinuteTimerEvent += Core_MinuteTimer;

            Network.CoreStatusChange += new StatusChange(Network_StatusChange);

            Core.Transfers.FileSearch[ServiceID, FileTypeData] += new FileSearchHandler(Transfers_DataFileSearch);
            Core.Transfers.FileRequest[ServiceID, FileTypeData] += new FileRequestHandler(Transfers_DataFileRequest);

            Core.Trust.LinkUpdate += new LinkUpdateHandler(Trust_Update);

            LocalFileKey = Core.User.Settings.FileKey;
            FileCrypt.Key = LocalFileKey;
            FileCrypt.IV = new byte[FileCrypt.IV.Length];

            string rootpath = Core.User.RootPath + Path.DirectorySeparatorChar + "Data" + Path.DirectorySeparatorChar + ServiceID.ToString() + Path.DirectorySeparatorChar;
            DataPath = rootpath + FileTypeData.ToString();
            WorkingPath = rootpath + FileTypeWorking.ToString();
            ResourcePath = rootpath + FileTypeResource.ToString();

            Directory.CreateDirectory(DataPath);
            Directory.CreateDirectory(WorkingPath);

            // clear resource files so that updates of these files work
            if (Directory.Exists(ResourcePath))
                Directory.Delete(ResourcePath, true);

            Cache = new VersionedCache(Network, ServiceID, FileTypeCache, false);

            Cache.FileAquired += new FileAquiredHandler(Cache_FileAquired);
            Cache.FileRemoved += new FileRemovedHandler(Cache_FileRemoved);
            Cache.Load();
            

            // load working headers
            OpStorage local = GetStorage(Core.UserID);

            foreach (uint project in Trust.LocalTrust.Links.Keys)
            {
                if (local != null)
                    LoadHeaderFile(GetWorkingPath(project), local, false, true);

                Working[project] = new WorkingStorage(this, project);

                bool doSave = false;
                foreach (ulong higher in Trust.GetAutoInheritIDs(Core.UserID, project))
                    if (Working[project].RefreshHigherChanges(higher))
                        doSave = true;

                Working[project].AutoIntegrate(doSave);
            }

            foreach (string testPath in Directory.GetFiles(DataPath))
                if (!ReferencedPaths.Contains(testPath))
                    try { File.Delete(testPath); }
                    catch { }

            ReferencedPaths.Clear();
            Loading = false;
        }
       

        public void Dispose()
        {
            if (Disposing != null)
                Disposing();

            HashFiles.Dispose();
            CopyFiles.Dispose();

            // lock down working
            List<LockError> errors = new List<LockError>();

            foreach (WorkingStorage working in Working.Values)
            {
                working.LockAll(errors);

                if(working.Modified)
                    working.SaveWorking();
            }
            Working.Clear();

            // delete completely folders made for other user's storages
            Trust.ProjectRoots.LockReading(delegate()
            {
                foreach (uint project in Trust.ProjectRoots.Keys)
                {
                    string path = Core.User.RootPath + Path.DirectorySeparatorChar + Trust.GetProjectName(project) + " Storage";
                    string local = Core.GetName(Core.UserID);

                    if (Directory.Exists(path))
                        foreach (string dir in Directory.GetDirectories(path))
                            if (Path.GetFileName(dir) != local)
                            {
                                try
                                {
                                    Directory.Delete(dir, true);
                                }
                                catch
                                {
                                    errors.Add(new LockError(dir, "", false, LockErrorType.Blocked));
                                }
                            }
                }
            });

            // security warning: could not secure these files
            if (errors.Count > 0)
            {
                string message = "Security Warning: Not able to delete these files, please do it manually\n";

                foreach (LockError error in errors)
                    if (error.Type == LockErrorType.Blocked)
                        message += error.Path;

                Core.UserMessage(message);
            }

            // kill events
            Core.SecondTimerEvent -= Core_SecondTimer;
            Core.MinuteTimerEvent -= Core_MinuteTimer;

            Network.CoreStatusChange -= new StatusChange(Network_StatusChange);

            Cache.FileAquired -= new FileAquiredHandler(Cache_FileAquired);
            Cache.FileRemoved -= new FileRemovedHandler(Cache_FileRemoved);
            Cache.Dispose();

            Core.Transfers.FileSearch[ServiceID, FileTypeData] -= new FileSearchHandler(Transfers_DataFileSearch);
            Core.Transfers.FileRequest[ServiceID, FileTypeData] -= new FileRequestHandler(Transfers_DataFileRequest);

            Core.Trust.LinkUpdate -= new LinkUpdateHandler(Trust_Update);
        }

        void Core_SecondTimer()
        {
            // every 10 seconds
            if (Core.TimeNow.Second % 9 == 0) 
                foreach(WorkingStorage working in Working.Values)
                    if (working.PeriodicSave)
                    {
                        working.SaveWorking();
                        working.PeriodicSave = false;
                    }
        }

        void Core_MinuteTimer()
        {
            // clear de-reffed files
            
            // not working reliable, un-reffed files cleared out when app loads
            /*FileMap.LockReading(delegate()
            {
                foreach (KeyValuePair<ulong, OpFile> pair in FileMap)
                    if (pair.Value.References == 0)
                        File.Delete(GetFilePath(pair.Key)); //crit test
            });*/
        }

        public void SimTest()
        {
            // create file
            // add file
            // accept file
            // integrate file


        }

        public void SimCleanup()
        {
            FileMap.SafeClear();
            InternalFileMap.SafeClear();

            WorkingStorage x = Working[0];

            StorageFolder packet = new StorageFolder();
            packet.Name = Core.Trust.GetProjectName(0) + " Files";
            x.RootFolder = new LocalFolder(null, packet);

            SaveLocal(0);
        }

        void Cache_FileRemoved(OpVersionedFile file)
        {
            OpStorage storage = GetStorage(file.UserID);

            if(storage != null)
                UnloadHeaderFile(GetFilePath(storage), storage.File.Header.FileKey);

            StorageMap.SafeRemove(file.UserID);
        }

        public void CallFolderUpdate(uint project, string dir, ulong uid, WorkingChange action)
        {
            if (WorkingFolderUpdate != null)
                Core.RunInGuiThread(WorkingFolderUpdate, project, dir, uid, action);
        }

        public void CallFileUpdate(uint project, string dir, ulong uid, WorkingChange action)
        {
            if (WorkingFileUpdate != null)
                Core.RunInGuiThread(WorkingFileUpdate, project, dir, uid, action);
        }

        public void SaveLocal(uint project)
        {
            try
            {
                string tempPath = Core.GetTempPath();
                byte[] key = Utilities.GenerateKey(Core.StrongRndGen, 256);

                using (IVCryptoStream stream = IVCryptoStream.Save(tempPath, key))
                {
                    // write loaded projects
                    WorkingStorage working = null;
                    if (Working.ContainsKey(project))
                        working = Working[project];

                    if (working != null)
                    {
                        Protocol.WriteToFile(new StorageRoot(working.ProjectID), stream);
                        working.WriteWorkingFile(stream, working.RootFolder, true);

                        working.Modified = false;

                        try { File.Delete(GetWorkingPath(project)); }
                        catch { }

                    }

                    // open old file and copy entries, except for working
                    OpStorage local = GetStorage(Core.UserID);

                    if (local != null)
                    {
                        string oldPath = GetFilePath(local);

                        if (File.Exists(oldPath))
                        {
                            using (TaggedStream file = new TaggedStream(oldPath, Network.Protocol))
                            using (IVCryptoStream crypto = IVCryptoStream.Load(file, local.File.Header.FileKey))
                            {

                                PacketStream oldStream = new PacketStream(crypto, Protocol, FileAccess.Read);
                                bool write = false;
                                G2Header g2header = null;

                                while (oldStream.ReadPacket(ref g2header))
                                {
                                    if (g2header.Name == StoragePacket.Root)
                                    {
                                        StorageRoot root = StorageRoot.Decode(g2header);

                                        write = (root.ProjectID != project);
                                    }

                                    //copy packet right to new file
                                    if (write) //crit test
                                        stream.Write(g2header.Data, g2header.PacketPos, g2header.PacketSize);
                                }
                            }
                        }
                    }

                    stream.WriteByte(0); // signal last packet

                    stream.FlushFinalBlock();
                }

                SavingLocal = true; // prevents auto-integrate from re-calling saveLocal
                OpVersionedFile vfile = Cache.UpdateLocal(tempPath, key, BitConverter.GetBytes(Core.TimeNow.ToUniversalTime().ToBinary()));
                SavingLocal = false;

                Store.PublishDirect(Core.Trust.GetLocsAbove(), Core.UserID, ServiceID, FileTypeCache, vfile.SignedHeader);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Storage", "Error updating local " + ex.Message);
            }

            if (StorageUpdate != null)
                Core.RunInGuiThread(StorageUpdate, GetStorage(Core.UserID));

        }

        void Cache_FileAquired(OpVersionedFile file)
        {
            // unload old file
            OpStorage prevStorage = GetStorage(file.UserID);
            if (prevStorage != null)
            {
                string oldPath = GetFilePath(prevStorage);
                
                UnloadHeaderFile(oldPath, prevStorage.File.Header.FileKey);
            }

            OpStorage newStorage = new OpStorage(file);

            StorageMap.SafeAdd(file.UserID, newStorage);


            LoadHeaderFile(GetFilePath(newStorage), newStorage, false, false);

            // record changes of higher nodes for auto-integration purposes
            Trust.ProjectRoots.LockReading(delegate()
            {
                foreach (uint project in Trust.ProjectRoots.Keys)
                {
                    List<ulong> inheritIDs = Trust.GetAutoInheritIDs(Core.UserID, project);

                    if (Core.UserID == newStorage.UserID || inheritIDs.Contains(newStorage.UserID))
                        // doesnt get called on startup because working not initialized before headers are loaded
                        if (Working.ContainsKey(project))
                        {
                            bool doSave = Working[project].RefreshHigherChanges(newStorage.UserID);

                            if (!Loading && !SavingLocal)
                                Working[project].AutoIntegrate(doSave);
                        }
                }
            });

            // update subs - this ensures file not propagated lower until we have it (prevents flood to original poster)
            if (Network.Established)
            {
                List<LocationData> locations = new List<LocationData>();

                Trust.ProjectRoots.LockReading(delegate()
                {
                    foreach (uint project in Trust.ProjectRoots.Keys)
                        if (newStorage.UserID == Core.UserID || Trust.IsHigher(newStorage.UserID, project))
                            Trust.GetLocsBelow(Core.UserID, project, locations);
                });

                Store.PublishDirect(locations, newStorage.UserID, ServiceID, FileTypeCache, file.SignedHeader);
            }

            if (StorageUpdate != null)
                Core.RunInGuiThread(StorageUpdate, newStorage);

            if (Core.NewsWorthy(newStorage.UserID, 0, false))
                Core.MakeNews(ServiceIDs.Storage, "File System updated by " + Core.GetName(newStorage.UserID), newStorage.UserID, 0, false);

        }

        void Trust_Update(OpTrust trust)
        {
            // update working projects (add)
            if (trust.UserID == Core.UserID)
            {
                OpStorage local = GetStorage(Core.UserID);

                foreach (uint project in Trust.LocalTrust.Links.Keys)
                    if (!Working.ContainsKey(project))
                    {
                        if(local != null)
                            LoadHeaderFile(GetWorkingPath(project), local, false, true);
                        
                        Working[project] = new WorkingStorage(this, project);
                    }
            }

            // remove all higher changes, reload with new highers (cause link changed
            foreach (WorkingStorage working in Working.Values )
                if (Core.UserID == trust.UserID || Trust.IsHigher(trust.UserID, working.ProjectID))
                {
                    working.RemoveAllHigherChanges();

                    foreach (ulong uplink in Trust.GetAutoInheritIDs(Core.UserID, working.ProjectID))
                        working.RefreshHigherChanges(uplink);
                }
        }

        bool Transfers_DataFileSearch(ulong key, FileDetails details)
        {
            ulong hashID = BitConverter.ToUInt64(details.Hash, 0);

            OpFile file = null;

            if (FileMap.SafeTryGetValue(hashID, out file))
                if (details.Size == file.Size && Utilities.MemCompare(details.Hash, file.Hash))
                    return true;

            return false;
        }

        string Transfers_DataFileRequest(ulong key, FileDetails details)
        {
            ulong hashID = BitConverter.ToUInt64(details.Hash, 0);
            
            OpFile file = null;

            if (FileMap.SafeTryGetValue(hashID, out file))
                if (details.Size == file.Size && Utilities.MemCompare(details.Hash, file.Hash))
                    return GetFilePath(hashID);

            return null;
        }

        void Network_StatusChange()
        {
            if (!Network.Established)
                return;

            // trigger download of files now in cache range
            StorageMap.LockReading(delegate()
            {
                foreach (OpStorage storage in StorageMap.Values)
                    if (Network.Routing.InCacheArea(storage.UserID))
                        LoadHeaderFile(GetFilePath(storage), storage, true, false);
            });
        }

        public void Research(ulong key)
        {
            Cache.Research(key);
        }

        public string GetFilePath(OpStorage storage)
        {
            return Cache.GetFilePath(storage.File.Header);
        }

        public string GetFilePath(ulong hashID)
        {
            ICryptoTransform transform = FileCrypt.CreateEncryptor();

            byte[] hash = BitConverter.GetBytes(hashID);

            return DataPath + Path.DirectorySeparatorChar + Utilities.ToBase64String(transform.TransformFinalBlock(hash, 0, hash.Length));
        }

        public string GetWorkingPath(uint project)
        {
            return WorkingPath + Path.DirectorySeparatorChar + Utilities.CryptFilename(Core, "working:" + project.ToString());
        }

        public OpStorage GetStorage(ulong key)
        {
            OpStorage storage = null;

            StorageMap.SafeTryGetValue(key, out storage);

            return storage;
        }

        private void LoadHeaderFile(string path, OpStorage storage, bool reload, bool working)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                bool cached = Network.Routing.InCacheArea(storage.UserID);
                bool local = false;

                byte[] key = working ? LocalFileKey : storage.File.Header.FileKey;

                using (TaggedStream filex = new TaggedStream(path, Network.Protocol))
                using (IVCryptoStream crypto = IVCryptoStream.Load(filex, key))
                {
                    PacketStream stream = new PacketStream(crypto, Protocol, FileAccess.Read);

                    G2Header header = null;

                    ulong currentUID = 0;

                    while (stream.ReadPacket(ref header))
                    {
                        if (!working && header.Name == StoragePacket.Root)
                        {
                            StorageRoot packet = StorageRoot.Decode(header);

                            local = Core.UserID == storage.UserID ||
                                    GetHigherRegion(Core.UserID, packet.ProjectID).Contains(storage.UserID) ||
                                    Trust.GetDownlinkIDs(Core.UserID, packet.ProjectID, 1).Contains(storage.UserID);
                        }

                        if (header.Name == StoragePacket.File)
                        {
                            StorageFile packet = StorageFile.Decode(header);

                            if (packet == null)
                                continue;

                            bool historyFile = true;
                            if (packet.UID != currentUID)
                            {
                                historyFile = false;
                                currentUID = packet.UID;
                            }

                            OpFile file = null;
                            if (!FileMap.SafeTryGetValue(packet.HashID, out file))
                            {
                                file = new OpFile(packet);
                                FileMap.SafeAdd(packet.HashID, file);
                            }

                            InternalFileMap.SafeAdd(packet.InternalHashID, file);

                            if (!reload)
                                file.References++;

                            if (!working) // if one ref is public, then whole file is marked public
                                file.Working = false;

                            if (packet.HashID == 0 || packet.InternalHash == null)
                            {
                                Debug.Assert(false);
                                continue;
                            }

                            string filepath = GetFilePath(packet.HashID);
                            file.Downloaded = File.Exists(filepath);

                            if (Loading && file.Downloaded && !ReferencedPaths.Contains(filepath))
                                ReferencedPaths.Add(filepath);

                            if (!file.Downloaded)
                            {
                                // if in local range only store latest 
                                if (local && !historyFile)
                                    DownloadFile(storage.UserID, packet);

                                // if storage is in cache range, download all files
                                else if (Network.Established && cached)
                                    DownloadFile(storage.UserID, packet);
                            }

                            // on link update, if in local range, get latest files
                            // (handled by location update, when it sees a new version of storage component is available)                 
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Storage", "Error loading files " + ex.Message);
            }
        }

        public void DownloadFile(ulong id, StorageFile file)
        {
            // called from hash thread
            if (Core.InvokeRequired)
            {
                Core.RunInCoreAsync(delegate() { DownloadFile(id, file); });
                return;
            }

            // if file still processing return
            if (file.Hash == null)
                return;

 
            FileDetails details = new FileDetails(ServiceID, FileTypeData, file.Hash, file.Size, null);

            Core.Transfers.StartDownload(id, details, GetFilePath(file.HashID), new EndDownloadHandler(EndDownloadFile), new object[] { file });
        }

        private void EndDownloadFile(object[] args)
        {
            StorageFile file = (StorageFile) args[0];

            OpFile commonFile = null;
            if (FileMap.SafeTryGetValue(file.HashID, out commonFile))
                commonFile.Downloaded = true;

            // interface list box would be watching if file is transferring, will catch completed update
        }

        private void UnloadHeaderFile(string path, byte[] key)
        {
            try
            {
                if (!File.Exists(path))
                    return;

                using (TaggedStream filex = new TaggedStream(path, Network.Protocol))
                using (IVCryptoStream crypto = IVCryptoStream.Load(filex, key))
                {
                    PacketStream stream = new PacketStream(crypto, Protocol, FileAccess.Read);

                    G2Header header = null;

                    while (stream.ReadPacket(ref header))
                    {
                        if (header.Name == StoragePacket.File)
                        {
                            StorageFile packet = StorageFile.Decode(header);

                            if (packet == null)
                                continue;

                            OpFile commonFile = null;
                            if (!FileMap.SafeTryGetValue(packet.HashID, out commonFile))
                                continue;

                            commonFile.DeRef();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Storage", "Error loading files " + ex.Message);
            }
        }

        public void MarkforHash(LocalFile file, string path, uint project, string dir)
        {
            HashPack pack = new HashPack(file, path, project, dir);

            lock (HashFiles.Pending)
                if (HashFiles.Pending.Any(p => ((HashPack)p.Param2).File == file))
                    return;

            file.Info.Size = new FileInfo(path).Length; // set so we can get hash status

            HashFiles.Enqueue(() => HashFile(pack), pack);
        }

 
        void HashFile(HashPack pack)
        {
            // three steps, hash file, encrypt file, hash encrypted file
            try
            {
                OpFile file = null;
                StorageFile info = pack.File.Info.Clone();

                // remove old references from local file
                OpFile commonFile = null;
                if (FileMap.SafeTryGetValue(pack.File.Info.HashID, out commonFile))
                    commonFile.DeRef(); //crit test
                
                if (!File.Exists(pack.Path))
                    return;

                // do public hash
                Utilities.ShaHashFile(pack.Path, ref info.InternalHash, ref info.InternalSize);
                info.InternalHashID = BitConverter.ToUInt64(info.InternalHash, 0);

                // if file exists in public map, use key for that file
                OpFile internalFile = null;
                InternalFileMap.SafeTryGetValue(info.InternalHashID, out internalFile);

                if (internalFile != null)
                {
                    file = internalFile;
                    file.References++;

                    // if file already encrypted in our system, continue
                    if (File.Exists(GetFilePath(info.HashID)))
                    {
                        info.Size = file.Size;
                        info.FileKey = file.Key;

                        info.Hash = file.Hash;
                        info.HashID = file.HashID;

                        if (!Utilities.MemCompare(file.Hash, pack.File.Info.Hash))
                            ReviseFile(pack, info);

                        return;
                    }
                }

                // file key is opID and public hash xor'd so that files won't be duplicated on the network
                // apply special compartment key here as well, xor again
                RijndaelManaged crypt = Utilities.CommonFileKey(Core.User.Settings.OpKey, info.InternalHash);
                info.FileKey = crypt.Key;

                // encrypt file to temp dir
                string tempPath = Core.GetTempPath();
                Utilities.EncryptTagFile(pack.Path, tempPath, crypt, Core.Network.Protocol, ref info.Hash, ref info.Size);
                info.HashID = BitConverter.ToUInt64(info.Hash, 0);

                // move to official path
                string path = GetFilePath(info.HashID);
                if (!File.Exists(path))
                    File.Move(tempPath, path);

                // if we dont have record of file make one
                if (file == null)
                {
                    file = new OpFile(info);
                    file.References++;
                    FileMap.SafeAdd(info.HashID, file);
                    InternalFileMap.SafeAdd(info.InternalHashID, file);
                }
                // else, record already made, just needed to put the actual file in the system
                else
                {
                    Debug.Assert(info.HashID == file.HashID);
                }

                
                // if hash is different than previous mark as modified
                if (!Utilities.MemCompare(file.Hash, pack.File.Info.Hash))
                    ReviseFile(pack, info);
            }
            catch (Exception ex)
            {
                /*rotate file to back of queue
                lock (HashQueue)
                    if (HashQueue.Count > 1)
                        HashQueue.Enqueue(HashQueue.Dequeue());*/

                Core.Network.UpdateLog("Storage", "Hash thread: " + ex.Message);
            }
        }
        

        private void ReviseFile(HashPack pack, StorageFile info)
        {
            // called from hash thread
            if (Core.InvokeRequired)
            {
                Core.RunInCoreAsync(() => ReviseFile(pack, info));
                return;
            }

            if (Working.ContainsKey(pack.Project))
                Working[pack.Project].ReadyChange(pack.File, info);

            CallFileUpdate(pack.Project, pack.Dir, info.UID, WorkingChange.Updated);
        }

        public string GetRootPath(ulong user, uint project)
        {
            return Core.User.RootPath + Path.DirectorySeparatorChar + Trust.GetProjectName(project) + " Storage" + Path.DirectorySeparatorChar + Core.GetName(user);
        }

        public WorkingStorage Discard(uint project)
        {
            if (Core.InvokeRequired)
            {
                WorkingStorage previous = null;
                Core.RunInCoreBlocked(delegate() { previous = Discard(project); });
                return previous;
            }

            if (!Working.ContainsKey(project))
                return null;

            // LockAll() to prevent unlocked discarded changes from conflicting with previous versions of
            // files when they are unlocked again by the user
            List<LockError> errors = new List<LockError>();
            Working[project].LockAll(errors);
            Working.Remove(project);

            // call unload on working
            string path = GetWorkingPath(project);
            UnloadHeaderFile(path, LocalFileKey);

            // delete working file
            try { File.Delete(path); }
            catch { };
                 
            //loadworking
            Working[project] = new WorkingStorage(this, project);

            if (StorageUpdate != null)
                Core.RunInGuiThread(StorageUpdate, GetStorage(Core.UserID));

            return Working[project];
        }

        public bool FileExists(StorageFile file)
        {
            if (FileMap.SafeContainsKey(file.HashID) &&
                File.Exists(GetFilePath(file.HashID)))
                return true;

            return false;
        }

        public string DownloadStatus(StorageFile file)
        {
            // returns null if file not being handled by transfer component

            if (file.Hash == null) // happens if file is being added to storage
                return null;

            return Core.Transfers.GetDownloadStatus(ServiceID, file.Hash, file.Size);
        }

        public bool IsFileUnlocked(ulong dht, uint project, string path, StorageFile file, bool history)
        {
            string finalpath = GetRootPath(dht, project) + path;

            if (history)
                finalpath += Path.DirectorySeparatorChar + ".history" + Path.DirectorySeparatorChar + GetHistoryName(file);
            else
                finalpath += Path.DirectorySeparatorChar + file.Name;

            return File.Exists(finalpath);
        }

        public bool IsHistoryUnlocked(ulong dht, uint project, string path, ThreadedLinkedList<StorageItem> archived)
        {
            string finalpath = GetRootPath(dht, project) + path + Path.DirectorySeparatorChar + ".history" + Path.DirectorySeparatorChar;

            bool result = false;

            if (Directory.Exists(finalpath))
                archived.LockReading(delegate()
                {
                    foreach (StorageFile file in archived)
                        if (File.Exists(finalpath + GetHistoryName(file)))
                        {
                            result = true;
                            break;
                        }
                });

            return result;
        }

        private string GetHistoryName(StorageFile file)
        {
            string name = file.Name;

            int pos = name.LastIndexOf('.');
            if (pos == -1)
                pos = name.Length;


            string tag = "unhashed";
            if(file.InternalHash != null)
                tag = Utilities.BytestoHex(file.InternalHash, 0, 3, false);

            name = name.Insert(pos, "-" + tag);

            return name;
        }

        public string UnlockFile(ulong dht, uint project, string path, StorageFile file, bool history, List<LockError> errors)
        {
            // path needs to include name, because for things like history files name is diff than file.Info

            string finalpath = GetRootPath(dht, project) + path;

            finalpath += history ? Path.DirectorySeparatorChar + ".history" + Path.DirectorySeparatorChar : Path.DirectorySeparatorChar.ToString();

            if (!CreateFolder(finalpath, errors, false))
                return null;
            
            finalpath += history ? GetHistoryName(file) : file.Name;


            // file not in storage
            if(!FileMap.SafeContainsKey(file.HashID) || !File.Exists(GetFilePath(file.HashID)))
            {
                errors.Add(new LockError(finalpath, "", true, LockErrorType.Missing));
                return null;
            }

            // check if already unlocked
            if (File.Exists(finalpath) && file.IsFlagged(StorageFlags.Unlocked))
                return finalpath;

            // file already exists
            if(File.Exists(finalpath))
            {
               
                // ask user about local
                if (dht == Core.UserID)
                {
                    errors.Add(new LockError(finalpath, "", true, LockErrorType.Existing, file, history));

                    return null;
                }

                // overwrite remote
                else
                {
                    try
                    {
                        File.Delete(finalpath);
                    }
                    catch
                    {
                        // not an existing error, dont want to give user option to 'use' the old remote file
                        errors.Add(new LockError(finalpath, "", true, LockErrorType.Unexpected, file, history));
                        return null;
                    }
                }
            }


            // extract file
            try
            {
                Utilities.DecryptTagFile(GetFilePath(file.HashID), finalpath, file.FileKey, Core);
            }
            catch (Exception ex)
            {
                Core.Network.UpdateLog("Storage", "UnlockFile: " + ex.Message);

                errors.Add(new LockError(finalpath, "", true, LockErrorType.Unexpected, file, history));
                return null;
            }
        

            file.SetFlag(StorageFlags.Unlocked);

            if (dht != Core.UserID)
            {
                //FileInfo info = new FileInfo(finalpath);
                //info.IsReadOnly = true;
            }

            // local
            else if (Working.ContainsKey(project) )
            {
                // let caller trigger event because certain ops unlock multiple files

                // set watch on root path
                Working[project].StartWatchers();
            }

            return finalpath;
        }

        public void LockFileCompletely(ulong dht, uint project, string path, ThreadedLinkedList<StorageItem> archived, List<LockError> errors)
        {
            if (archived.SafeCount == 0)
                return;

            StorageFile main = (StorageFile) archived.SafeFirst.Value;
            
            string dirpath = GetRootPath(dht, project) + path;

            // delete main file
            string finalpath = dirpath + Path.DirectorySeparatorChar + main.Name;

            if (File.Exists(finalpath))
                if (DeleteFile(finalpath, errors, false))
                    main.RemoveFlag(StorageFlags.Unlocked);

            // delete archived file
            finalpath = dirpath + Path.DirectorySeparatorChar + ".history" + Path.DirectorySeparatorChar;

            if (Directory.Exists(finalpath))
            {
                List<string> stillLocked = new List<string>();

                archived.LockReading(delegate()
                {
                    foreach (StorageFile file in archived)
                    {
                        string historyPath = finalpath + GetHistoryName(file);

                        if (File.Exists(historyPath))
                            if (DeleteFile(historyPath, errors, false))
                                file.RemoveFlag(StorageFlags.Unlocked);
                            else
                                stillLocked.Add(historyPath);
                    }
                });

                // delete history folder
                DeleteFolder(finalpath, errors, stillLocked);
            }
        }

        public bool DeleteFile(string path, List<LockError> errors, bool temp)
        {
            try
            {
                File.Delete(path);
            }
            catch(Exception ex)
            {
                errors.Add(new LockError(path, ex.Message, true, temp ? LockErrorType.Temp : LockErrorType.Blocked ));
                return false;
            }

            return true;
        }

        public void DeleteFolder(string path, List<LockError> errors, List<string> stillLocked)
        {
            try
            {
                if (Directory.GetDirectories(path).Length > 0 || Directory.GetFiles(path).Length > 0)
                {
                    foreach (string directory in Directory.GetDirectories(path))
                        if (stillLocked != null && !stillLocked.Contains(directory))
                            errors.Add(new LockError(directory, "", false, LockErrorType.Temp));

                    foreach (string file in Directory.GetFiles(path))
                        if (stillLocked != null && !stillLocked.Contains(file))
                            errors.Add(new LockError(file, "", true, LockErrorType.Temp));
                }
                else
                {
                    foreach (WorkingStorage working in Working.Values)
                        if (path == working.RootPath)
                            working.StopWatchers();

                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                errors.Add(new LockError(path, ex.Message, false,  LockErrorType.Blocked));
            }
        }

        public bool CreateFolder(string path, List<LockError> errors, bool subs)
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex)
            {
                LockError error = new LockError(path, ex.Message, true, LockErrorType.Unexpected);
                error.Subs = subs;
                errors.Add(error);

                return false;
            }

            return true;
        }

        public void LockFile(ulong dht, uint project, string path, StorageFile file, bool history)
        {
            string finalpath = GetRootPath(dht, project) + path;

            if (history)
                finalpath += Path.DirectorySeparatorChar + ".history" + Path.DirectorySeparatorChar + GetHistoryName(file);
            else
                finalpath += Path.DirectorySeparatorChar + file.Name;

            try
            {
                if (File.Exists(finalpath))
                    File.Delete(finalpath);

                file.RemoveFlag(StorageFlags.Unlocked);
            }
            catch { }
        }

        public StorageActions ItemDiff(StorageItem item, StorageItem original)
        {
            StorageActions actions = StorageActions.None;

            if (original == null)
                return StorageActions.Created;

            if (item.Name != original.Name)
                actions = actions | StorageActions.Renamed;

            if (ScopeChanged(item.Scope, original.Scope))
                actions = actions | StorageActions.Scoped;

            if (item.IsFlagged(StorageFlags.Archived) && !original.IsFlagged(StorageFlags.Archived))
                actions = actions | StorageActions.Deleted;

            if (!item.IsFlagged(StorageFlags.Archived) && original.IsFlagged(StorageFlags.Archived))
                actions = actions | StorageActions.Restored;

            if (item.GetType() == typeof(StorageFile))
                if (!Utilities.MemCompare(((StorageFile)item).InternalHash, ((StorageFile)original).InternalHash))
                    actions = actions | StorageActions.Modified;


            return actions;
        }

        public bool ScopeChanged(Dictionary<ulong, short> a, Dictionary<ulong, short> b)
        {
            if (a.Count != b.Count)
                return true;

            foreach (ulong id in a.Keys)
            {
                if (!b.ContainsKey(id))
                    return true;

                if (a[id] != b[id])
                    return true;
            }

            return false;
        }

        public List<ulong> GetHigherRegion(ulong id, uint project)
        {
            // all users form id to the top, and direct subs of superior

            List<ulong> highers = Trust.GetUplinkIDs(id, project); // works for loops

            highers.AddRange(Trust.GetAdjacentIDs(id, project));

            highers.Remove(id); // remove target


            return highers;
        }
    }

    public class OpStorage
    {
        public OpVersionedFile File;

        public OpStorage(OpVersionedFile file)
        {
            File = file;
        }

        public ulong UserID
        {
            get
            {
                return File.UserID;
            }
        }


        public DateTime Date
        {
            get
            {
                return DateTime.FromBinary(BitConverter.ToInt64(File.Header.Extra, 0));
            }
        }
    }

    public class OpFile
    {
        public long Size;
        public ulong HashID;
        public byte[] Key;
        public byte[] Hash;
        public int References;
        public bool Working;
        public bool Downloaded;

        public OpFile(StorageFile file)
        {
            HashID = file.HashID;
            Hash = file.Hash;
            Size = file.Size;
            Key = file.FileKey;
            Working = true;
        }

        public void DeRef()
        {
            if (References > 0)
                References--;
        }
    }

    public class HashPack
    {
        public LocalFile File;
        public string Path;
        public string Dir;
        public uint Project;
        

        public HashPack(LocalFile file, string path, uint project, string dir)
        {
            File = file;
            Path = path;
            Project = project;
            Dir = dir;
        }

        public override bool Equals(object obj)
        {
            HashPack pack = obj as HashPack;

            if(obj == null)
                return false;

            return (string.Compare(Path, pack.Path, true) == 0);
        }

        public override int GetHashCode()
        {
            return base.GetHashCode();
        }
    }
}
