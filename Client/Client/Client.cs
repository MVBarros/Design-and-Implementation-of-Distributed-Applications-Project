﻿using MSDAD.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Threading;

namespace MSDAD
{
    namespace Client
    {
        public delegate String parseDelegate();

        class Client : MarshalByRefObject
        {
            private readonly IMSDADServer Server;
            private readonly String UserId;
            private int milliseconds;

            public Client(IMSDADServer server, String userId)
            {
                this.Server = server;
                this.UserId = userId;
                this.milliseconds = 0;
            }

            private void ListMeetings()
            {
                SafeSleep();

                String meetings = Server.ListMeetings(this.UserId);
                
                Console.WriteLine(meetings);   
                
            }

            private void JoinMeeting(String topic, List<String> slots)
            {
                SafeSleep();
                try
                {
                    Server.JoinMeeting(topic, slots, this.UserId, DateTime.Now);
                } catch (MSDAD.Shared.ServerException e)
                {
                    Console.WriteLine(e.GetErrorMessage());
                }
            }

            private void CloseMeeting(String topic)
            {
                SafeSleep();
                try
                {
                    Server.CloseMeeting(topic, this.UserId);
                } catch(MSDAD.Shared.ServerException e)
                {
                    Console.Write(e.GetErrorMessage());
                } 
            }

            private void CreateMeeting(String topic, uint min_atendees, List<String> slots, HashSet<String> invitees)
            {
                SafeSleep();
                try
                {
                    Server.CreateMeeting(this.UserId, topic, min_atendees, slots, invitees);
                } catch(CannotCreateMeetingException e)
                {
                    Console.WriteLine(e.GetErrorMessage());
                } catch (LocationDoesNotExistException e)
                {
                    Console.WriteLine(e.GetErrorMessage());
                }
            }

            private void Wait(int milliseconds)
            {
                SafeSleep();
                this.milliseconds = milliseconds;
            }

            private void SafeSleep()
            {
                if (this.milliseconds != 0)
                {
                    Thread.Sleep(this.milliseconds);
                    this.milliseconds = 0;
                }
            }

            public void ParseScript(parseDelegate reader)
            {
                String line;
                while(( line = reader.Invoke() ) != null)
                {
                    String[] items = line.Split(' ');
                    switch(items[0])
                    {
                        case "list":
                            this.ListMeetings();
                            break;

                        case "close":
                            this.CloseMeeting(items[1]);
                            break;

                        case "join":
                            List<String> slots = new List<string>();
                            uint slotCount = UInt32.Parse(items[2]);
                            for (uint i = 3; i < 3 + slotCount; ++i)
                            {
                                slots.Add(items[i]);
                            }
                            this.JoinMeeting(items[1], slots);
                            break;

                        case "create":
                            int numSlots = Int32.Parse(items[3]);
                            int numInvitees = Int32.Parse(items[4]);

                            slots = new List<string>();
                            HashSet<String> invitees =  numInvitees == 0 ?  null : new HashSet<string>();
                            uint j;
                            for (j = 5; j < 5 + numSlots; ++j)
                            {
                                slots.Add(items[j]);
                            }
                            for (; j < 5 + numSlots + numInvitees; ++j)
                            {
                                invitees.Add(items[j]);
                            }
                            this.CreateMeeting(items[1], UInt32.Parse(items[2]), slots, invitees);
                            break;

                        case "wait":
                            this.Wait(Int32.Parse(items[1]));
                            break;

                        default:
                            Console.WriteLine("Invalid command: {0}", items[0]);
                            break;
                    }
                }

            } 

            static void Main(string[] args)
            {
                if(args.Length != 5)
                {
                    System.Console.WriteLine("<usage> Client username client_port network_name server_url script_file");
                    Environment.Exit(1);
                }

                TcpChannel channel = new TcpChannel(Int32.Parse(args[1]));
                ChannelServices.RegisterChannel(channel, false);
                IMSDADServer server = null;
                try
                {
                    server = (IMSDADServer)Activator.GetObject(typeof(IMSDADServer), args[3]);
                } catch (SocketException e)
                {
                    Console.WriteLine("Rip server");
                    Console.ReadLine();
                }

                if (server == null)
                {
                    System.Console.WriteLine("Server could not be contacted");
                    Environment.Exit(1);
                }
                else
                {
                    Client client = new Client(server, args[0]);
                    RemotingServices.Marshal(client, args[2], typeof(Client));

                    if (File.Exists(AppDomain.CurrentDomain.BaseDirectory + args[4]))
                    {
                        StreamReader reader = File.OpenText(AppDomain.CurrentDomain.BaseDirectory +  args[4]);
                        client.ParseScript(reader.ReadLine);
                        reader.Close();
                       
                    }
                    else
                    {
                        Console.WriteLine("Error: File provided does not exist");
                    }
                    client.ParseScript(Console.ReadLine);
                }
            }
        }
    }
}
