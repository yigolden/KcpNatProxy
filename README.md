# KcpNatProxy

KcpNatProxy is a reverse proxy to help you expose a local TCP/UDP server behind a NAT or firewall to the Internet. This main purpose of this project is to validate [KcpSharp](https://github.com/yigolden/KcpSharp) library design. Users seeking a similar project can refer to [fatedier/frp](https://github.com/fatedier/frp).

# Configuration File Sample

## Server Configuration
```
{
    "Listen": {
        "EndPoint": "your-server-address:6677",
        "Mtu": 1420
    },
    "Credential": "your-password",
    "Services": [
        {
            "Name": "rdp-tcp",
            "ServiceType": "Tcp",
            "Listen": "0.0.0.0:3389"
        },
        {
            "Name": "rdp-udp",
            "ServiceType": "Udp",
            "Listen": "0.0.0.0:3389"
        }
    ]
}
```

## Client Configuration
```
{
    "Connect": {
        "EndPoint": "your-server-address:6677",
        "Mtu": 1400
    },
    "Credential": "1234",
    "Providers": [
        {
            "Name": "rdp-tcp",
            "ServiceType": "Tcp",
            "Forward": "127.0.0.1:3389"
        },
        {
            "Name": "rdp-udp",
            "ServiceType": "Udp",
            "Forward": "127.0.0.1:3389"
        }
    ]
}
```
