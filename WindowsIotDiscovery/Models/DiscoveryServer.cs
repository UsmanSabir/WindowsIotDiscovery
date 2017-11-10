﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using WindowsIotDiscovery.Models.Messages;

namespace WindowsIotDiscovery.Models
{
    public class DiscoveryServer
    {
        /// <summary>
        /// A timer to periodically find smart devices on the network
        /// </summary>
        private Timer discoverSmartDevicesTimer;

        /// <summary>
        /// A socket to broadcast discovery requests
        /// </summary>
        private DatagramSocket socket;

        /// <summary>
        /// Port to send to and listen for UDP packets from other devices
        /// </summary>
        string udpPort;

        Subject<Unit> whenDevicesChanged = new Subject<Unit>();

        /// <summary>
        /// A list of all the devices the Discovery System is aware of
        /// </summary>
        public List<DiscoverableDevice> Devices { get; set; }

        /// <summary>
        /// IP Address of the DiscoveryServer
        /// </summary>
        public string IpAddress
        {
            get
            {
                var hosts = NetworkInformation.GetHostNames();
                foreach (var host in hosts)
                {
                    if (host.Type == HostNameType.Ipv4)
                    {
                        return host.DisplayName;
                    }
                }
                return "";
            }
        }

        public IObservable<Unit> WhenDevicesChanged => whenDevicesChanged;

        public DiscoveryServer(string udpPort)
        {
            Devices = new List<DiscoverableDevice>();
            this.udpPort = udpPort;
            socket = new DatagramSocket();
        }

        /// <summary>
        /// Initialize the Discovery System
        /// </summary>
        /// <returns></returns>
        public async void Initialize()
        {
            Debug.WriteLine("Discovery System: Initializing");

            try
            {
                // Set the message received function
                socket.MessageReceived += ReceiveDiscoveryResponse;

                // Start the server
                await socket.BindServiceNameAsync(udpPort);

                // Set a timer to discover new devices every minute
                discoverSmartDevicesTimer = new Timer(SendDiscoveryRequest, null, 0, 60000);

                Debug.WriteLine($"Discovery System: Initialized on port {udpPort}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Discovery System: Failure");
                Debug.WriteLine("Reason: " + ex.Message);
            }
        }

        /// <summary>
        /// Callback fired when a packet is received on the port.
        /// </summary>
        /// <param name="ds"></param>
        /// <param name="args"></param>
        /// Sample Message {"IpAddress":"10.0.0.202","Product":"PotPiServer","Command":"DiscoveryRequest"}
        /// Sample Message {"IpAddress":"10.0.0.202","Product":"PotPiPowerBox","SerialNumber":"1234-abcd","TcpPort":"215"}
        public async void ReceiveDiscoveryResponse(DatagramSocket ds, DatagramSocketMessageReceivedEventArgs args)
        {
            Debug.WriteLine("Discovery System: Received UDP packet");

            try
            {
                // Get the data from the packet
                var resultStream = args.GetDataStream().AsStreamForRead();
                using (var reader = new StreamReader(resultStream))
                {
                    string discoveryResponseString = await reader.ReadToEndAsync();
                    JObject jDiscoveryResponse = JObject.Parse(discoveryResponseString);
                    Debug.WriteLine($"   >>> {discoveryResponseString}");

                    // Ignore if this is a discovery request
                    if (jDiscoveryResponse["command"] != null && jDiscoveryResponse.Value<string>("command") == "DISCOVER")
                    {
                        Debug.WriteLine("Discovery System: Ignoring discovery request");
                        return;
                    }

                    // The device must broadcast a name and its device info
                    if (jDiscoveryResponse["name"] != null &&
                       jDiscoveryResponse["deviceInfo"] != null)
                    {
                        // Create a strongly typed model of this new device
                        var newDevice = new DiscoverableDevice();
                        newDevice.DeviceInfo = jDiscoveryResponse.Value<JObject>("deviceInfo");
                        newDevice.Name = jDiscoveryResponse.Value<string>("name");
                        newDevice.IpAddress = args.RemoteAddress.DisplayName;

                        // Go through the existing devices
                        foreach (var device in Devices)
                        {
                            if(device.Name == newDevice.Name)
                            { 
                                // Silence the device to avoid repeat responses
                                SilenceSmartDevice(newDevice.IpAddress + jDiscoveryResponse.Value<string>("silenceUrl"));

                                // If the IP address has changed
                                if (device.IpAddress != newDevice.IpAddress)
                                {
                                    // Update the smart device in the database
                                    device.IpAddress = newDevice.IpAddress;

                                    // Let everyone know
                                    whenDevicesChanged.OnNext(Unit.Default);

                                    return;
                                }
                                else // If its a perfect match
                                {
                                    // Ignore the response
                                    return;
                                }
                            }
                        }

                        // Silence the device to avoid repeat responses
                        SilenceSmartDevice(newDevice.IpAddress + jDiscoveryResponse.Value<string>("silenceUrl"));

                        // Add it to the database
                        Debug.WriteLine($"Discovery System: Added {newDevice.Name} @ {newDevice.IpAddress}");
                        Devices.Add(newDevice);

                        // Let everyone know
                        whenDevicesChanged.OnNext(Unit.Default);
                    }
                    else // If the response was not valid
                    {
                        Debug.WriteLine("Discovery System: UDP packet not valid");
                        // Ignore the packet
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Discovery System - Failure: " + ex.Message);
            }
        }

        /// <summary>
        /// Sends a discovery request UDP packet
        /// </summary>
        public async void SendDiscoveryRequest(object state = null)
        {
            Debug.WriteLine("Discovery System: Sending Discovery Request");
            try
            {
                // Get an output stream to all IPs on the given port
                using (var stream = await socket.GetOutputStreamAsync(new HostName("255.255.255.255"), udpPort))
                {
                    // Get a data writing stream
                    using (var writer = new DataWriter(stream))
                    {
                        // Include all known devices in the request to minimize traffic (smart devices can use this info to determine if they need to respond)
                        JArray jDevices = new JArray();
                        foreach (var device in Devices)
                        {
                            jDevices.Add(device);
                        }

                        // Create a discovery request message
                        DiscoveryRequestMessage discoveryRequestMessage = new DiscoveryRequestMessage("DISCOVER", "Server", IpAddress, jDevices);

                        // Convert the request to a JSON string
                        writer.WriteString(JsonConvert.SerializeObject(discoveryRequestMessage));

                        Debug.WriteLine($"   >>> {JsonConvert.SerializeObject(discoveryRequestMessage)}");

                        // Send
                        await writer.StoreAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Discovery System Server - Send Discovery Request Failed: " + ex.Message);
            }
        }

        private async void SilenceSmartDevice(string apiUrl)
        {
            Debug.WriteLine("Discovery System: Silencing device.");
            Debug.WriteLine($"   >>> {apiUrl}");

            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetAsync("http://" + apiUrl);
            }
        }
    }
}