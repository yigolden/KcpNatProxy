# KcpNatProxy

KcpNatProxy is a reverse proxy to help you expose a local TCP/UDP server behind a NAT or firewall to the Internet. This main purpose of this project is to validate [KcpSharp](https://github.com/yigolden/KcpSharp) library design. Users seeking a similar project can refer to [fatedier/frp](https://github.com/fatedier/frp).

# Configuration File Sample

## Server Configuration
```
{
    "Listen": "0.0.0.0:8899",
    "Password": "your-password", 
    "Mtu": 1460
}
```

## Client Configuration
```
{
    "EndPoint": "your-server-address:8899",
    "Password": "your-password",
    "Mtu": 1460,
    "Services":[
        {
            "Name": "rdp-tcp",
            "Type": "tcp",
            "RemoteListen": "0.0.0.0:3389",
            "LocalForward": "127.0.0.1:3389",
            "NoDelay": true
        },
        {
            "Name": "rdp-udp",
            "Type": "udp",
            "RemoteListen": "0.0.0.0:3389",
            "LocalForward": "127.0.0.1:3389"
        }
    ]
}
```