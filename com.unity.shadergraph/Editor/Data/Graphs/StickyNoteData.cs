using System;
using UnityEngine;

namespace UnityEditor.ShaderGraph
{
    [Serializable]
    public class StickyNoteData : ISerializationCallbackReceiver
    {
        [NonSerialized]
        Guid m_Guid;

        public Guid guid => m_Guid;

        [SerializeField]
        string m_GuidSerialized;

        [SerializeField]
        string m_Title;

        public string title
        {
            get => m_Title;
            set => m_Title = value;
        }

        [SerializeField]
        string m_Content;

        public string content
        {
            get => m_Content;
            set => m_Content = value;
        }

        [SerializeField]
        string m_Text;

        public string text
        {
            get => m_Text;
            set => m_Text = value;
        }

        [SerializeField]
        Vector2 m_Position;

        public Vector2 position
        {
            get => m_Position;
            set => m_Position = value;
        }

        public StickyNoteData(string title, string content, Vector2 position)
        {
            m_Guid = Guid.NewGuid();
            m_Title = title;
            m_Position = position;
            m_Content = content;
        }

        public void OnBeforeSerialize()
        {
            m_GuidSerialized = guid.ToString();
        }

        public void OnAfterDeserialize()
        {
            if (!string.IsNullOrEmpty(m_GuidSerialized))
            {
                m_Guid = new Guid(m_GuidSerialized);
            }
        }
    }
}

