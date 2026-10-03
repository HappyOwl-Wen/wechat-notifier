namespace WeChatNotifier.Core;

public enum MessageKind
{
    Unknown = 0,
    Text,
    Image,
    Sticker,
    Voice,
    Video,
    File,
    Link,
    Location,
    ContactCard,
    Call,
    RedPacket,
    Transfer,
    MiniProgram,
    ChatHistory,
    Music,
}
