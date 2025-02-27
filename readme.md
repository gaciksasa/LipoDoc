## Database Schema

The application uses the following database schema:

### DeviceData
Stores the lipemic test results from blood donation devices:
- ID (auto-generated primary key)
- DeviceId (serial number of the device)
- Timestamp (when data was received)
- MessageType (the type of message: "#S", "#D", etc.)
- RawPayload (the raw message content)
- IPAddress (source IP)
- Port (source port)
- DeviceStatus (0=IDLE, 1=Process in progress, 2=Process completed)
- AvailableData (number of readings buffered in the device)
- IsBarcodeMode (whether barcode mode is enabled)
- RefCode (reference code barcode)
- DonationIdBarcode (donation ID barcode)
- OperatorIdBarcode (operator ID barcode)
- LotNumber (lot number barcode)
- LipemicValue (value of lipemic reading)
- LipemicGroup (lipemic group: I, II, III, or IV)
- LipemicStatus (LIPEMIC or PASSED)
- CheckSum (checksum from the device)

### DeviceStatus
Stores the status updates from devices:
- ID (auto-generated primary key)
- DeviceId (serial number of the device)
- Timestamp (when status was received)
- Status (0=IDLE, 1=Process in progress, 2=Process completed)
- AvailableData (number of readings buffered in the device)
- RawPayload (the raw message content)
- IPAddress (source IP)
- Port (source port)
- CheckSum (checksum from the device)

### Device
Stores information about registered devices:
- ID (auto-generated primary key)
- SerialNumber (unique identifier for the device)
- Name (friendly name for the device)
- Location (location of the device)
- LastConnectionTime (last time the device connected)
- RegisteredDate (when the device was first registered)
- IsActive (whether the device is active)
- Notes (additional notes about the device)

### Users
Stores user authentication information:
- ID (auto-generated primary key)
- Username (unique username)
- PasswordHash (hashed password)
- Role (Admin or User)
- FullName (full name of the user)
- Email (email address)
- CreatedAt (when the user was created)
- LastLogin (last login time)## Authentication and Authorization

The application includes a role-based authentication system with two predefined roles:

1. **Admin Role**
   - Full access to all features
   - Can view, add, edit, and delete data
   - Username: `admin`, Password: `admin123`

2. **User Role**
   - Limited access to application features
   - Can view and add data but cannot edit or delete
   - Username: `user`, Password: `user123`

### Security Features

- Password hashing using BCrypt
- Cookie-based authentication
- Role-based authorization policies
- Secure routing with authorization attributes
- Protected views that adapt based on user role# Device Data Collector

A .NET 8 web application for collecting, storing, and managing data from TCP/IP devices.

## Project Overview

This application serves as a central data collection point for IoT or networked devices that communicate via TCP/IP. It runs a TCP server that listens for incoming connections, stores received data in a MySQL database, and provides a web interface for viewing and managing the collected data.

## Features

- **TCP/IP Server**: Listens on a configurable port (default: 5000) for incoming connections
- **Device Communication**: Send and receive messages from connected devices
- **Data Storage**: Automatically stores incoming data in a MySQL database
- **Web Interface**: View, create, edit, and delete stored device data
- **Live Communication**: Test device connectivity by sending manual messages

## System Architecture

The application consists of:

1. **Web Interface** (ASP.NET MVC)
2. **TCP Server** (Background Service)
3. **MySQL Database** (via Entity Framework Core)

## Getting Started

### Prerequisites

- .NET 8.0 SDK
- MySQL Server
- Visual Studio 2022 or later (recommended)

### Configuration

Database and TCP server settings can be configured in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "server=localhost;port=3306;database=devicedata;user=root;password=root"
  },
  "TCPServer": {
    "IPAddress": "127.0.0.2",
    "Port": 5000
  }
}
```

> **Important**: Before running the application, ensure you've configured the loopback addresses (127.0.0.2 and 127.0.0.3) as described in the "Configuring Additional Local IP Addresses" section below.

### Running the Application

1. Clone the repository
2. Update the database connection string in `appsettings.json`
3. Configure the required loopback IP addresses (127.0.0.2 and 127.0.0.3) as described in the "Configuring Additional Local IP Addresses" section below
4. Run the application:
   ```
   dotnet run
   ```

The application will:
- Automatically create the database and tables if they don't exist
- Apply any pending migrations
- Start the TCP server listening on the configured IP address (127.0.0.2) and port (5000)

> Note: Manual migration using `dotnet ef database update` is no longer required as the application handles this automatically at startup.

## Key Components

### Controllers

- **HomeController**: Main application entry point
- **DeviceController**: Handles device communication
- **DeviceDataController**: Manages stored device data

### Services

- **TCPServerService**: Background service that maintains a TCP listener and processes incoming connections
- **TCPClientService**: Handles outgoing TCP connections to devices

### Data Model

- **DeviceData**: Represents a single data packet received from a device
  - ID (auto-generated)
  - DeviceID (string identifier)
  - Timestamp (when data was received)
  - DataPayload (the actual data content)
  - IPAddress (source IP)
  - Port (source port)

## Usage

### Viewing Collected Data

Navigate to the "View Data" page to see all data collected from devices.

### Sending Messages to Devices

Use the "Send Data" page to manually send messages to devices:

1. Enter the device IP address and port
2. Type your message
3. Click "Send Message"

The system will attempt to connect to the device, send the message, and display any response.

### Testing with TCP Client Tools

For testing, you can use tools like:

- **Hercules** (Set to 127.0.0.3 in the default configuration)
- **NetCat**
- **Telnet**

## Development Notes

- The application is configured to distinguish between messages sent from the web interface and actual device data.
- Messages sent via the web interface are not stored in the database.
- The server automatically adds a newline character to messages if not present.

## Configuring Additional Local IP Addresses

For testing purposes, you may need to set up additional loopback IP addresses (like 127.0.0.2, 127.0.0.3, etc.) on your local machine. The application requires two different loopback addresses:

1. **127.0.0.2** - Used by the application's TCP server (configured in `appsettings.json`)
2. **127.0.0.3** - Typically used for testing clients like Hercules

Here's how to add these addresses:

### Windows

1. Open Command Prompt as Administrator
2. Add a new loopback IP address using the following command:
   ```
   netsh interface ipv4 add address "Loopback Adapter" 127.0.0.3 255.0.0.0
   ```
   Replace "Loopback Adapter" with your loopback adapter name if different.
   
3. Verify the IP address was added:
   ```
   netsh interface ipv4 show ipaddresses
   ```

4. To make this persistent across reboots, create a batch file with the command and add it to your startup items.

### Linux

1. Temporarily add a loopback IP address:
   ```
   sudo ip addr add 127.0.0.3/8 dev lo
   ```

2. Verify it was added:
   ```
   ip addr show dev lo
   ```

3. To make it persistent, add to `/etc/network/interfaces`:
   ```
   auto lo:0
   iface lo:0 inet static
       address 127.0.0.3
       netmask 255.0.0.0
   ```

### macOS

1. Temporarily add a loopback IP address:
   ```
   sudo ifconfig lo0 alias 127.0.0.3/8
   ```

2. Verify it was added:
   ```
   ifconfig lo0
   ```

3. To make it persistent, create a launch daemon.

## Database Management

The project uses Entity Framework Core with a Code-First approach and automatically handles database creation and migration at startup.

### Automatic Database Initialization

The application now includes a `DatabaseInitializer` service that:
- Creates the database if it doesn't exist
- Applies any pending migrations automatically
- Logs the database initialization process

### Managing Migrations

For development purposes, you can still manage migrations manually:

```bash
# Add a new migration after model changes
dotnet ef migrations add MigrationName

# Apply migrations manually (not required for normal operation)
dotnet ef database update
```

## Project Structure

- **Controllers/**: MVC controllers
- **Models/**: Data models
- **Views/**: User interface
- **Services/**: Background services and utilities
- **Data/**: Database context and configuration
- **Migrations/**: Database migration files

## License

[Your license information]

## Contributors

[Your contributor information]
