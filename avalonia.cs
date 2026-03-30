// 第一次启动应用，生成一个 UUID，存到本地用户目录，以后一直使用

var path = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    "yourapp",
    "device_id.txt"
);

Directory.CreateDirectory(Path.GetDirectoryName(path));

string deviceId;

if (File.Exists(path))
{
    deviceId = File.ReadAllText(path);
}
else
{
    deviceId = Guid.NewGuid().ToString();
    File.WriteAllText(path, deviceId);
}

// 数据库表：user_devices
// 字段：device_id、first_login_time、last_login_time、user_id
