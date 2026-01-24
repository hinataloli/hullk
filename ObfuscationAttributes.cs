using System.Reflection;

// Áp dụng obfuscation mức cao cho toàn bộ assembly
[assembly: Obfuscation(Feature = "all", Exclude = false, ApplyToMembers = true)]

// Bật string encryption + compression (rất hữu ích cho RAT)
[assembly: Obfuscation(Feature = "string encryption", Exclude = false)]

// Bật control flow obfuscation (làm rối luồng điều khiển)
[assembly: Obfuscation(Feature = "control flow", Exclude = false)]

// Bật resource protection (ẩn tài nguyên, string, XAML nếu có)
[assembly: Obfuscation(Feature = "resources protection", Exclude = false)]

// Nếu muốn bật code virtualization (chỉ Pro version, bỏ comment nếu có license)
// [assembly: Obfuscation(Feature = "virtualization", Exclude = false)]

// Nếu có class/method dùng reflection nặng (như SocketIOClient), exclude để tránh crash
// [assembly: Obfuscation(Feature = "all", Exclude = true, Target = typeof(TenClassDungReflection))]