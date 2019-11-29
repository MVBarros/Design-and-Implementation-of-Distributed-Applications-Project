﻿using MSDAD.Shared;
using System;
using System.Collections.Generic;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Runtime.Remoting.Messaging;

namespace MSDAD
{
    namespace Server
    {
        class Server : MarshalByRefObject, IMSDADServer, IMSDADServerPuppet, IMSDADServerToServer
        {

            /*System members*/
            private readonly ConcurrentDictionary<String, IMSDADServerToServer> ServerView = new ConcurrentDictionary<String, IMSDADServerToServer>();
            private readonly ConcurrentDictionary<String, String> ServerNames = new ConcurrentDictionary<String, String>();

            private readonly ConcurrentDictionary<ServerClient, byte> ClientURLs = new ConcurrentDictionary<ServerClient, byte>();
            /*****************************/

            /*Server properties*/
            private readonly ConcurrentDictionary<String, Meeting> Meetings = new ConcurrentDictionary<string, Meeting>();
            private readonly String ServerId;
            private readonly String ServerUrl;
            private readonly uint MaxFaults;
            private readonly int MinDelay;
            private readonly int MaxDelay;
            /****************************/

            private bool LeaderToken { get; set; }
            private static readonly Object CloseMeetingLock = new object();
            private static readonly Random random = new Random();

            /*Delegates for Async calls*/
            public delegate ConcurrentDictionary<String, Meeting> ListAsyncDelegate();
            public delegate Meeting RemoteAsyncDelegate(String topic);
            public delegate void JoinAsyncDelegate(String topic, List<string> slots, String userId, DateTime timestamp);
            public delegate void MergeMeetingDelegate(String topic, Meeting meeting);
            /************************************/

            /*Properties for Reliable Broadcast*/
            public delegate void RBSendDelegate(String messageId, String operation, object[] args);
            public ConcurrentDictionary<String, CountdownEvent> RBMessages = new ConcurrentDictionary<string, CountdownEvent>();
            public int RBMessageCounter = 0;
            /***********************************/


            
            public Server(String ServerId, uint MaxFaults, int MinDelay, int MaxDelay, String ServerUrl)
            {
                this.ServerId = ServerId;
                this.MaxFaults = MaxFaults;
                this.MinDelay = MinDelay;
                this.MaxDelay = MaxDelay;
                this.ServerUrl = ServerUrl;
            }

            static void Main(string[] args)
            {

                if (args.Length < 9)
                {
                    Console.WriteLine("<Usage> Server server_ip server_id network_name port max_faults min_delay max_delay num_servers server_urls numLocations locations");
                    Console.WriteLine(" Press < enter > to shutdown server...");
                    Console.ReadLine();
                    return;
                }
                String ServerUrl = "tcp://" + args[0] + ":" + args[3] + "/" + args[2];
                
                Console.WriteLine(String.Format("[SETUP] Server with id {0} Initializing  with url {1}", args[1], ServerUrl));

                //Initialize Server
                TcpChannel channel = new TcpChannel(Int32.Parse(args[3]));
                ChannelServices.RegisterChannel(channel, false);
                Server server = new Server(args[1], UInt32.Parse(args[4]), Int32.Parse(args[5]), Int32.Parse(args[6]), ServerUrl);
                RemotingServices.Marshal(server, args[2], typeof(Server));

                //Get Server URLS and connect to them
                int i;
                for (i = 8; i < 8 + Int32.Parse(args[7]); ++i)
                {
                    Console.WriteLine("[SETUP] Connecting to server with url {0}", args[i]);

                    IMSDADServerToServer otherServer = (IMSDADServerToServer)Activator.GetObject(typeof(IMSDADServer), args[i]);
                    if (otherServer != null)
                    {
                        //FIXME Review: we don't need Server State since all the servers are created on the beginning
                        // of the system and none crashes while the system is setup
                        Console.WriteLine(String.Format("[SETUP] Successfully connected to server with url {0} successfully", args[i]));
                        String id = otherServer.NewServer(server.ServerId, server.ServerUrl);
                        server.ServerView[id] =  otherServer;
                    }
                    else
                    {
                        //Should never happen
                        Console.WriteLine(String.Format("[ERROR] Could not connect to server at address {0}", args[i]));
                    }

                }

                //Means that it is the first server to be created
                if (server.ServerView.Count == 0)
                {
                    server.LeaderToken = true;
                }

                //Create Locations
                int j = i + 1;
                
                for (i = j; i < j + 3 * Int32.Parse(args[j - 1]); i += 3)
                {
                    Console.WriteLine(String.Format("[SETUP] Adding room: {0} {1} {2}", args[i], args[i + 1], args[i + 2]));
                    ((IMSDADServerPuppet)server).AddRoom(args[i], UInt32.Parse(args[i + 1]), args[i + 2]);
                }

                Console.WriteLine(String.Format("[SETUP] Setup successfull!\n[SETUP] [FINISH] ip: {0} ServerId: {1} network_name: {2} port: {3} max faults: {4} min delay: {5} max delay: {6}", args[0], args[1], args[2], args[3], args[4], args[5], args[6]));
                Console.WriteLine("[SHUTDOWN] Press < enter > to shutdown server...");
                Console.ReadLine();
            }


            //Client Leases never expire
            public override object InitializeLifetimeService()
            {
                return null;
            }

            
            private void SafeSleep()
            {
                int mili = random.Next(MinDelay, MaxDelay);
                if (mili != 0)
                {
                    Thread.Sleep(mili);
                }
                return;
            }

            HashSet<ServerClient> IMSDADServer.CreateMeeting(string topic, Meeting meeting)
            {
                SafeSleep();
                Console.WriteLine(String.Format("[INFO] [NEW-MEETING] Broadcast meeting with topic {0} to other servers", topic));
                //FIXME We should make it causally ordered
                ((IMSDADServerToServer)this).RB_Send(RBNextMessageId(), "CreateMeeting", new object[] { topic, meeting });
                Console.WriteLine(String.Format("[INFO] [NEW-MEETING] [FINISH] Meeting with topic {0} broadcasted successfully", topic));
                return this.GetMeetingInvitees(this.Meetings[topic]);
            }

            HashSet<ServerClient> GetMeetingInvitees(Meeting meeting)
            {
                //Give Client URLS of other clients that can join
                //FIXME Should create meeting difusion algorithm here
                HashSet<ServerClient> clients = new HashSet<ServerClient>();
                foreach (ServerClient client in ClientURLs.Keys)
                {
                    if (meeting.CanJoin(client.ClientId))
                    {
                        clients.Add(client);
                    }
                }
                return clients;
            }


            void IMSDADServerToServer.JoinMeeting(String topic, List<String> slots, String userId, DateTime timestamp)
            {
                Console.WriteLine(String.Format("[INFO] [JOIN-MEETING] Join of user {0} to meeting with topic {1} reached this server", userId, topic));
                SafeSleep();
                bool found = Meetings.TryGetValue(topic, out Meeting meeting);

                //FIXME Join will be causal with create and as such this will never happen
                if (!found)
                {
                    Console.WriteLine(String.Format("[ERROR] [JOIN-MEETING] Join of user {0} to meeting with topic {1} " +
                        "cannot be processed as meeting as not reached the server", userId, topic));
                    return;
                    //throw new NoSuchMeetingException("Meeting specified does not exist on this server");
                }

                //See if Meeting as reached the server yet
                if (meeting.CurState != Meeting.State.Open)
                {
                    return;
                    //FIXME Maybe just return since this is called by another server?
                    //throw new CannotJoinMeetingException("Meeting is no longer open");
                }

                //Join Client to Meeting
                lock (Meetings.Keys.FirstOrDefault(k => k.Equals(topic)))
                {
                    if(!meeting.CanJoin(userId))
                    {
                        //FIXME Just print error info since it should not happen
                        Console.WriteLine(String.Format("[ERROR] [JOIN-MEETING] Client {0} will join meeting with topic {1} without an invite", userId, topic));
                        //throw new CannotJoinMeetingException("User " + userId + " cannot join this meeting.\n");
                    }

                    List<Slot> givenSlots = Slot.ParseSlots(slots);
                    foreach (Slot slot in meeting.Slots.Where(x => givenSlots.Contains(x)))
                    {

                        slot.AddUserId(userId, timestamp);

                    }
                    meeting.AddUser(userId, timestamp);
                }
                Console.WriteLine(String.Format("[INFO] [JOIN-MEETING] [FINISH] user {0} joined meeting with topic {1}", userId, topic));
                return;
            }


            //FIXME Maybe can have a more generic method to send something to F servers like we have for reliable broadcast

            void IMSDADServer.JoinMeeting(string topic, List<string> slots, string userId, DateTime timestamp)
            {
                SafeSleep();
                Console.WriteLine(String.Format("[INFO] [JOIN-MEETING] User {0} wants to join meeting with topic {1}", userId, topic));
                bool found = Meetings.TryGetValue(topic, out Meeting meeting);

                //Meeting hasn't reached the server yet
                if (!found)
                {
                    Console.WriteLine(String.Format("[ERROR] [JOIN-MEETING] Meeting with topic {0} hasn't reached this server yet, so user {1} must retry later", topic, userId));
                    throw new NoSuchMeetingException("Meeting specified does not exist on this server");
                }

                CountdownEvent latch = new CountdownEvent((int)this.MaxFaults);
                ((IMSDADServerToServer)this).JoinMeeting(topic, slots, userId, timestamp);

                
                Console.WriteLine(String.Format("[INFO] [JOIN-MEETING] Propagate join of user {0} to meeting with topic {1} to {2} servers", userId, topic, this.MaxFaults));

                //Propagate the join and wait for maxFaults responses
                //FIXME Should be causal send Join Meeting
                foreach (IMSDADServerToServer otherServer in this.ServerView.Values)
                {
                    JoinAsyncDelegate RemoteDel = new JoinAsyncDelegate(otherServer.JoinMeeting);
                    AsyncCallback RemoteCallback = new AsyncCallback(ar =>
                    {
                        JoinAsyncDelegate del = (JoinAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
                        del.EndInvoke(ar);
                        lock (latch)
                        {
                            if (!latch.IsSet){
                                latch.Signal();
                                Console.WriteLine(String.Format("[ACK] [JOIN-MEETING] Ack of join of user {0} to meeting with topic {1}. {2} Acks left to go",
                                    userId, topic, latch.CurrentCount));

                            }
                        }
                    });
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(topic, slots, userId, timestamp, RemoteCallback, null);
                }
                
                latch.Wait();
                //Cannot dispose latch because callback uses it

                Console.WriteLine(String.Format("[INFO] [JOIN-MEETING] [FINISH] User {0} joined meeting with topic {1} finished", userId, topic));
                return;
            }

            IDictionary<String, Meeting> IMSDADServer.ListMeetings(Dictionary<String, Meeting> meetings)
            {
                //FIXME Should ask f servers for the state of the meetings given.
                SafeSleep();
                Console.WriteLine("[INFO] [LIST-MEETINGS] Received List Meetings request");
                ListMeetingsMerge(meetings);

                CountdownEvent latch = new CountdownEvent((int)this.MaxFaults);
                Console.WriteLine(String.Format("[INFO] [LIST-MEETINGS] Will query {0} servers before return", this.MaxFaults));
                
                foreach (IMSDADServerToServer otherServer in this.ServerView.Values)
                {
                    ListAsyncDelegate RemoteDel = new ListAsyncDelegate(otherServer.GetMeetings);
                    AsyncCallback RemoteCallback = new AsyncCallback(ar =>
                    {
                        ListAsyncDelegate del = (ListAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
                        ConcurrentDictionary<String, Meeting> serverMeeting = del.EndInvoke(ar);
                        lock (latch)
                        {
                            //Only merge meetings for f servers, then return
                            if (!latch.IsSet)
                            {
                                ListMeetingsMerge(serverMeeting);
                                latch.Signal();
                                Console.WriteLine(String.Format("[ACK] [LIST-MEETINGS] Ack of Get Meetings only {0} to go", latch.CurrentCount));
                            }
                        }
                    }
                    );
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(RemoteCallback, null);
                }
                latch.Wait();
                //Cannot dispose latch because callback uses it

                Console.WriteLine(String.Format("[INFO] [LIST-MEETINGS] [FINISH] List meetings query finished", this.MaxFaults));
                return this.Meetings;
            }

            void IMSDADServerToServer.CloseMeeting(String topic, Meeting meeting)
            {
                //Lock other threads from closing any Meetings
                lock (CloseMeetingLock)
                {
                    Meetings[topic] = meeting;
                    //Lock other threads from joining or updating this meeting
                    lock (Meetings.Keys.FirstOrDefault(k => k.Equals(topic)))
                    {

                        List<Slot> slots = meeting.Slots.Where(x => x.GetNumUsers() >= meeting.MinParticipants).ToList();

                        if (slots.Count == 0)
                        {
                            meeting.CurState = Meeting.State.Canceled;
                            PropagateClosedMeeting(topic, meeting);
                            return;
                        }

                        slots.Sort((x, y) =>
                        {
                            return (int)(y.GetNumUsers() - x.GetNumUsers());
                        });

                        uint numUsers = slots[0].GetNumUsers();
                        DateTime date = slots[0].Date;

                        //Only those with maximum potential users
                        slots = slots.Where(x => x.GetNumUsers() == numUsers).ToList();

                        //Tightest room
                        slots.Sort((x, y) =>
                        {
                            return (int)(x.Location.GetBestFittingRoomForCapacity(date, numUsers).Capacity - y.Location.GetBestFittingRoomForCapacity(date, numUsers).Capacity);
                        });

                        meeting.Close(slots[0], numUsers);
                    }
                    PropagateClosedMeeting(topic, meeting);
                }
            }

            void PropagateClosedMeeting(String topic, Meeting meeting)
            {
                Object objLock = new Object();
                CountdownEvent latch = new CountdownEvent(this.ServerView.Count);

                //Propagate the closed meeting to all the servers and await for all responses
                foreach (IMSDADServerToServer otherServer in this.ServerView.Values)
                {
                    MergeMeetingDelegate RemoteDel = new MergeMeetingDelegate(otherServer.MergeClosedMeeting);
                    AsyncCallback RemoteCallback = new AsyncCallback(ar =>
                    {
                        lock (objLock)
                        {
                            MergeMeetingDelegate del = (MergeMeetingDelegate)((AsyncResult)ar).AsyncDelegate;
                            del.EndInvoke(ar);
                            latch.Signal();
                        }
                    });
                    IAsyncResult RemAr = RemoteDel.BeginInvoke(topic, meeting, RemoteCallback, null);
                }
                latch.Wait();
                latch.Dispose();

            }

            void IMSDADServerToServer.MergeClosedMeeting(string topic, Meeting meeting)
            {
                lock (Meetings.Keys.FirstOrDefault(k => k.Equals(topic)))
                {
                    //Update Meeting and book room if Meeting was closed
                    Meetings[topic] = meeting;
                    meeting.BookClosedMeeting();
                }
            }
            
            void IMSDADServerPuppet.AddRoom(String location, uint capacity, String roomName)
            {
                Console.WriteLine(String.Format("[INFO] [ADD-ROOM] Received new room: location {0}, room {1} capacity {2}", location, roomName, capacity));
                //Block Other threads from Adding Rooms as well
                lock (Location.Locations)
                {
                    Location local = Location.FromName(location);
                    if (local == null)
                    {
                        local = new Location(location);
                        Location.AddLocation(local);
                    }
                    local.AddRoom(new Room(roomName, capacity));
                }
                Console.WriteLine(String.Format("[INFO] [ADD-ROOM] [FINISH] Room location {0}, room {1} capacity {2} finished", location, roomName, capacity));

            }
            void IMSDADServerPuppet.Crash()
            {
                Environment.Exit(1);
            }
            void IMSDADServerPuppet.Freeze()
            {
                throw new NotImplementedException();
            }
            void IMSDADServerPuppet.Unfreeze()
            {
                throw new NotImplementedException();
            }
            void IMSDADServerPuppet.Status()
            {
                foreach (IMSDADServerToServer server in this.ServerView.Values)
                {
                    try
                    {
                        String ping = server.Ping();
                        Console.WriteLine(ping);
                    }
                    catch (RemotingException)
                    {
                        Console.WriteLine("[ERROR] [PING] Could not contact Server");
                    }
                }
            }

            String IMSDADServerToServer.Ping()
            {
                SafeSleep();
                return String.Format("Server with id {1} is alive at url {0}", this.ServerUrl, this.ServerId);
            }


            Dictionary<String,String> IMSDADServer.NewClient(string url, string id)
            {
                Console.WriteLine("[INFO] [NEW-CLIENT] Received New Client connection request: client: <id:{0} ; url:{1}>", id, url);
            
                ServerClient client = new ServerClient(url, id);
                
                if (!ClientURLs.ContainsKey(client))
                {
                    Console.WriteLine(String.Format("[INFO] [NEW-CLIENT] First time seeing client <id:{0} ; url:{1}>, will Broadcast", id, url));
                
                    ((IMSDADServerToServer)this).RB_Send(RBNextMessageId(), "NewClient", new object[] { client });
                }
                else
                {
                    Console.WriteLine(String.Format("[INFO] [NEW-CLIENT] Already know about client <id:{0} ; url:{1}>, do not need to broadcast", id, url));
                }
                
                Console.WriteLine(String.Format("[INFO] [NEW-CLIENT] [FINISH] Client <id:{0} ; url:{1}> connected successfully, will give known servers urls", id, url));

                //Give Known Servers to Client
                return ServerNames.ToDictionary(entry => entry.Key, entry => entry.Value); ;
                }

            void IMSDADServerToServer.NewClient(ServerClient client)
            {
                Console.WriteLine(String.Format("[INFO] [NEW-CLIENT] Broadcast of client <id:{0} ; url:{1}> reached server {2}", client.ClientId, client.Url, this.ServerId));
                this.ClientURLs.TryAdd(client, new byte());
                Console.WriteLine(String.Format("[INFO] [NEW-CLIENT] [FINISH] Client <id:{0} ; url:{1}> added to server {2}", client.ClientId, client.Url, this.ServerId));
            }

            String IMSDADServerToServer.NewServer(String id, string url)
            {
                Console.WriteLine("[INFO] [NEW-SERVER] Trying to Connect to server at address {0}", url);
                IMSDADServerToServer otherServer = (IMSDADServerToServer)Activator.GetObject(typeof(IMSDADServer), url);
                if (otherServer != null)
                {
                    ServerView.TryAdd(id, otherServer);
                    ServerNames.TryAdd(id, url);
                    Console.WriteLine("[INFO] [NEW-SERVER] [FINISH] Successfully connected to server at address {0}", url);
                }
                else
                {
                    Console.WriteLine("[ERROR] [NEW-SERVER] Cannot connect to server at address {0}", url);
                }
                return this.ServerId;
            }
            
            void IMSDADServerPuppet.ShutDown()
            {
                Environment.Exit(1);
            }

            void IMSDADServerToServer.CreateMeeting(String topic, Meeting meeting)
            {
                Console.WriteLine(String.Format("[INFO] [NEW-MEETING] Meeting with topic {0} reached server with id {1}", topic, this.ServerId));
                Meetings.TryAdd(topic, meeting);
                Console.WriteLine(String.Format("[INFO] [NEW-MEETING] [FINISH] Meeting with topic {0} added to server with id {1}", topic, this.ServerId));

            }

            Meeting IMSDADServerToServer.LockMeeting(string topic)
            {
                //Lock meeting as fase one of closing a meeting
                lock (Meetings.Keys.FirstOrDefault(k => k.Equals(topic)))
                {
                    this.Meetings[topic].CurState = Meeting.State.Pending;
                    return Meetings[topic];
                }
            }

            //FIXME Must change this
            void IMSDADServer.CloseMeeting(string topic, string userId)
            {
                Console.WriteLine(String.Format("Client with id {0} wants to close meeting with topic {1}", userId, topic));
                lock (Meetings.Keys.FirstOrDefault(k => k.Equals(topic)))
                {
                    SafeSleep();
                    Object objLock = new Object();
                    CountdownEvent latch = new CountdownEvent(this.ServerView.Count);
                    //Lock users from joining local meeting
                    this.Meetings[topic].CurState = Meeting.State.Pending;

                    //Lock users from joining a meeting on the other servers
                    foreach (IMSDADServerToServer otherServer in this.ServerView.Values)
                    {
                        RemoteAsyncDelegate RemoteDel = new RemoteAsyncDelegate(otherServer.LockMeeting);
                        AsyncCallback RemoteCallback = new AsyncCallback(ar =>
                        {
                            lock (objLock)
                            {
                                RemoteAsyncDelegate del = (RemoteAsyncDelegate)((AsyncResult)ar).AsyncDelegate;
                                this.Meetings[topic] = this.Meetings[topic].MergeMeeting(del.EndInvoke(ar));
                                latch.Signal();
                            }
                        });
                        IAsyncResult RemAr = RemoteDel.BeginInvoke(topic, RemoteCallback, null);
                    }
                    latch.Wait();
                    latch.Dispose();

                    //Leader can now close Meeting
                    if (LeaderToken)
                    {
                        ((IMSDADServerToServer)this).CloseMeeting(topic, this.Meetings[topic]);
                    }
                    else
                    {
                        IMSDADServerToServer leader = this.ServerView[this.ServerView.Keys.Min()];
                        MergeMeetingDelegate del = new MergeMeetingDelegate(leader.CloseMeeting);
                        del.BeginInvoke(topic, this.Meetings[topic], null, null);
                    }
                }
            }

            //Aux functions for list meetings
            ConcurrentDictionary<String, Meeting> IMSDADServerToServer.GetMeetings()
            {
                Console.WriteLine("[INFO] [LIST-MEETINGS] [FINISH] Got request to send my meetings");
                return Meetings;
            }

            //Aux function to merge lists of meetings
            void ListMeetingsMerge(IDictionary<String, Meeting> meetings)
            {
                foreach (String key in meetings.Keys.ToList())
                {
                    bool found = this.Meetings.TryGetValue(key, out Meeting myMeeting);

                    //If a client has a meeting I don't know about get that meeting
                    if (!found)
                    {
                        Meetings.TryAdd(key, meetings[key]);
                    }
                    else
                    {
                        //Merge meeting on the server and give the merged meeting to the client as well

                        lock (Meetings.Keys.FirstOrDefault(k => k.Equals(key)))
                        {
                            Meeting upToDate = myMeeting.MergeMeeting(meetings[key]);
                            this.Meetings[key] = upToDate;
                            //TODO Is this okay if its a list from a server
                            meetings[key] = upToDate;
                        }
                    }
                }
            }

            /***********************************************************************************************************************/
            /*************************************************Reliable Broadcast****************************************************/
            /***********************************************************************************************************************/

            private String RBNextMessageId()
            {
                return String.Format("{0}-{1}", this.ServerId, Interlocked.Increment(ref this.RBMessageCounter));
            }

            /// <summary>
            /// Reliably Broadcast a Method, meaning that certainly if the method is executed then 
            /// it will certainly be executed on all servers that are correct(i.e that do not crash)
            /// Algorithm: If it is the first time this message is seen on this server then broadcast it to all and wait for f acknowledges
            /// where f is the number of faults. When f servers received the message means that the message will never be lost on the system
            /// </summary>
            /// <param name="messageId"> unique id that identifies every message sent on the server </param>
            /// <param name="operation"> the method to run on the server</param>
            /// <param name="args"> arguments for the method to be ran</param>
            void IMSDADServerToServer.RB_Send(string messageId, string operation, object[] args)
            {
                if (RBMessages.TryAdd(messageId, new CountdownEvent((int)MaxFaults)))
                {
                    //First time seeing this message, rebroadcast and wait for F acks
                    foreach (IMSDADServerToServer server in this.ServerView.Values)
                    {
                        RBSendDelegate remoteDel = new RBSendDelegate(server.RB_Send);
                        IAsyncResult RemAr = remoteDel.BeginInvoke(messageId, operation, args, null, null);
                    }
                    RBMessages[messageId].Wait();
                    GetType().GetInterface("IMSDADServerToServer").GetMethod(operation).Invoke(this, args);
                }
                else
                {
                    //Already seen this message, ack
                    lock (RBMessages[messageId])
                    {
                        if (!RBMessages[messageId].IsSet)
                        {
                            RBMessages[messageId].Signal();
                        }
                    }
                }
            }
        }
    }
}