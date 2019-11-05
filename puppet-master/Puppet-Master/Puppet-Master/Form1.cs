﻿using System;
using System.Collections.Generic;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;
using System.Windows.Forms;
using MSDAD.Shared;

namespace Puppet_Master
{

    public partial class Form1 : Form
    {
        private List<PuppetRoom> Locations;
        public Dictionary<String, String> Clients;
        public Dictionary<PuppetServer, IMSDADServerPuppet> Servers;
        private TcpChannel channel;
        private FolderBrowserDialog FolderBrowser = new FolderBrowserDialog();
        public Form1()
        {
            InitializeComponent();
            this.channel = new TcpChannel(10001);
            ChannelServices.RegisterChannel(channel, false);
            this.Clients = new Dictionary<string, string>();
            this.Servers = new Dictionary<PuppetServer, IMSDADServerPuppet>();
            this.Locations = new List<PuppetRoom>();
        }

        private void textBox1_TextChanged(object sender, EventArgs e)
        {

        }


        private void AddRoom(String location, uint capacity, String name)
        {
            this.Locations.Add(new PuppetRoom(location, capacity, name));

            foreach(IMSDADServerPuppet serverURL in this.Servers.Values)
            {
                serverURL.AddRoom(location, capacity, name);   
            }
        }

        private void CreateServer(String[] url, String serverId, String maxFaults, String minDelay, String maxDelay)
        {
            //Contact PCS
            String ip = url[0];
            IMSDADPCS pcs = (IMSDADPCS)Activator.GetObject(typeof(IMSDADPCS), "tcp://" + ip + ":10000/PCS");

            if (pcs != null)
            {
                String args = String.Format("{0} {1} {2} {3} {4} {5}", serverId, url[2], url[1], maxFaults, minDelay, maxDelay);
                
                //Give Server URL of all Servers currently running
                String servers = "";
                foreach (PuppetServer server in this.Servers.Keys) {
                    servers += server.serverUrl + " ";
                }

                args += String.Format(" {0} {1}", this.Servers.Values.Count, servers);

                //Give Server URL of all Clients currently joined
                String clients = "";
                
                foreach (String client in this.Clients.Values)
                {
                    clients += client + " ";
                }

                args += String.Format("{0} {1}", this.Clients.Values.Count, clients);

                //Contact Server and get Ref
                pcs.CreateProcess("Server", args);
                String fullUrl = "tcp://" + ip + ":" + url[1] + "/" + url[2];
                IMSDADServerPuppet remoteRef = (IMSDADServerPuppet)Activator.GetObject(typeof(IMSDADServerPuppet), fullUrl);
                if (remoteRef != null)
                {
                    this.Servers.Add(new PuppetServer(serverId , fullUrl), remoteRef);
                }
                else
                {
                    this.textBox1.Text += String.Format("Server at URL {0} was not created", fullUrl);
                }
            }
            else
            {
                this.textBox1.Text += String.Format("Unable to contact PCS at ip {0}", ip);
            }
        }

        private String[] ParseUrl(String url)
        {
            String[] Items = url.Split(':');
            String ip = Items[1].Substring(2);
            String port = Items[2].Split('/')[0];
            String networkName = Items[2].Split('/')[1];
            return new string[] { ip, port, networkName };
        }
        private void ParseCommand(String command)
        {
            String[] items = command.Split();
            switch (items[0])
            {
                case "Server":
                    String[] url = ParseUrl(items[2]);
                    String serverId = items[1];
                    String maxFaults = items[3];
                    String minDelay = items[4];
                    String maxDelay = items[5];
                    CreateServer(url, serverId, maxFaults, minDelay, maxDelay);
                    break;
                case "Client":
                    break;
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            ParseCommand(this.CommandBox.Text);
            //Do method Async
            this.textBox1.Text += this.CommandBox.Text + "\r\n";
            this.CommandBox.Text = "";
        }

        private void CommandBox_TextChanged(object sender, EventArgs e)
        {

        }

        private void button1_Click_1(object sender, EventArgs e)
        {
            if (FolderBrowser.ShowDialog() == DialogResult.OK)
            {

            }
        }

        private void button1_Click_2(object sender, EventArgs e)
        {

        }

        private void button1_Click_3(object sender, EventArgs e)
        {

        }
    }
    public class PuppetRoom
    {
        public String location { get; }
        public uint capacity { get; }
        public String name { get; }

        public PuppetRoom(String location, uint capacity, String name)
        {
            this.location = location;
            this.capacity = capacity;
            this.name = name;
        }

        public override string ToString()
        {
            return String.Format("{0} {1} {2}", this.location, this.capacity, this.name);
        }
    }

    public class PuppetServer
    {
        public String serverId { get; }
        public String serverUrl { get; }

        public PuppetServer(String serverId, String serverUrl)
        {
            this.serverId = serverId;
            this.serverUrl = serverUrl;
        }
        public override bool Equals(Object obj)
        {
            //Check for null and compare run-time types.
            if ((obj == null) || !this.GetType().Equals(obj.GetType()))
            {
                return false;
            }
            else
            {
                PuppetServer s = (PuppetServer)obj;
                return s.serverId == this.serverId;
            }
        }
        public override int GetHashCode()
        {
            return this.serverId.GetHashCode();
        }

    }
    }

